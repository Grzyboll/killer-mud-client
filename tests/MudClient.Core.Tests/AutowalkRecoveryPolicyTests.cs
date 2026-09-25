using MudClient.Core.Automation;
using MudClient.Core.Gmcp;

namespace MudClient.Core.Tests;

public sealed class AutowalkRecoveryPolicyTests
{
    [Fact]
    public void GetGateOpeningCommands_KnocksBeforeOtherAttempts()
    {
        Assert.Equal(
            ["zapukaj", "pull", "pociagnij", "uderz"],
            AutowalkRecoveryPolicy.GetGateOpeningCommands());
    }

    [Fact]
    public void GetLowMovementAction_AtTenPercent_UsesMemorizedRefresh()
    {
        var spells = new[]
        {
            new MemorizedSpell(1, 3, "Refresh", Memed: true, Meming: false),
        };

        var action = AutowalkRecoveryPolicy.GetLowMovementAction(10, 100, spells);

        Assert.Equal(LowMovementAction.CastRefresh, action);
    }

    [Fact]
    public void GetLowMovementAction_AtTenPercent_WithoutReadyRefresh_Rests()
    {
        var spells = new[]
        {
            new MemorizedSpell(1, 3, "refresh", Memed: false, Meming: true),
        };

        var action = AutowalkRecoveryPolicy.GetLowMovementAction(5, 50, spells);

        Assert.Equal(LowMovementAction.Rest, action);
    }

    [Fact]
    public void GetLowMovementAction_AboveTenPercent_DoesNothing()
    {
        var action = AutowalkRecoveryPolicy.GetLowMovementAction(11, 100, []);

        Assert.Equal(LowMovementAction.None, action);
    }

    [Fact]
    public void GetLowMovementAction_CustomThreshold_UsesItInsteadOfTenPercent()
    {
        // 20/100 = 20% is above the default 10% threshold, but at/below a configured 25% one.
        Assert.Equal(
            LowMovementAction.None,
            AutowalkRecoveryPolicy.GetLowMovementAction(20, 100, []));
        Assert.Equal(
            LowMovementAction.Rest,
            AutowalkRecoveryPolicy.GetLowMovementAction(20, 100, [], thresholdPercent: 25));
    }

    [Theory]
    [InlineData("Brama jest zamknięta na klucz.")]
    [InlineData("Brama jest zamknieta na klucz.")]
    [InlineData("Brama jest zamknięta.")]
    [InlineData("Brama jest zamknieta.")]
    [InlineData("\u001b[31mBrama jest zamknięta na klucz.\u001b[0m")]
    public void IsLockedGateMessage_AcceptsPolishAndAsciiVariants(string line)
    {
        Assert.True(AutowalkRecoveryPolicy.IsLockedGateMessage(line));
    }

    [Theory]
    [InlineData("Drzwi są zamknięte.")]
    [InlineData("Brama otwiera się.")]
    public void IsLockedGateMessage_RejectsOtherLines(string line)
    {
        Assert.False(AutowalkRecoveryPolicy.IsLockedGateMessage(line));
    }

    [Theory]
    [InlineData("fighting")]
    [InlineData("Fighting")]
    [InlineData("FIGHTING")]
    public void IsCombatPosition_RecognizesFighting(string position)
    {
        Assert.True(AutowalkRecoveryPolicy.IsCombatPosition(position));
    }

    [Theory]
    [InlineData("standing")]
    [InlineData("resting")]
    [InlineData("sitting")]
    [InlineData("")]
    [InlineData(null)]
    public void IsCombatPosition_RejectsNonCombatPositions(string? position)
    {
        Assert.False(AutowalkRecoveryPolicy.IsCombatPosition(position));
    }

    [Theory]
    [InlineData("sitting")]
    [InlineData("Sitting")]
    [InlineData("SITTING")]
    public void IsSittingPosition_RecognizesSitting(string position)
    {
        Assert.True(AutowalkRecoveryPolicy.IsSittingPosition(position));
    }

    [Theory]
    [InlineData("standing")]
    [InlineData("fighting")]
    [InlineData("resting")]
    [InlineData("")]
    [InlineData(null)]
    public void IsSittingPosition_RejectsOtherPositions(string? position)
    {
        Assert.False(AutowalkRecoveryPolicy.IsSittingPosition(position));
    }

    [Theory]
    [InlineData("resting")]
    [InlineData("Resting")]
    [InlineData("RESTING")]
    public void IsRestingPosition_RecognizesResting(string position)
    {
        Assert.True(AutowalkRecoveryPolicy.IsRestingPosition(position));
    }

    [Theory]
    [InlineData("standing")]
    [InlineData("fighting")]
    [InlineData("sitting")]
    [InlineData("")]
    [InlineData(null)]
    public void IsRestingPosition_RejectsOtherPositions_IncludingSitting(string? position)
    {
        // "resting" (the "rest" command) is a distinct GMCP position from "sitting" ("sit") — the
        // group order is "rest", so it must not fire on a plain sit.
        Assert.False(AutowalkRecoveryPolicy.IsRestingPosition(position));
    }

    [Theory]
    [InlineData("standing", true)]
    [InlineData("Standing", true)]
    [InlineData("sitting", false)]
    [InlineData("fighting", false)]
    [InlineData(null, false)]
    public void IsStandingPosition_RecognizesOnlyStanding(string? position, bool expected)
    {
        Assert.Equal(expected, AutowalkRecoveryPolicy.IsStandingPosition(position));
    }

    [Fact]
    public void IsMemorizingSpell_SpellCurrentlyMeming_ReturnsTrue()
    {
        var spells = new[] { new MemorizedSpell(1, 3, "heal", Memed: false, Meming: true) };

        Assert.True(AutowalkRecoveryPolicy.IsMemorizingSpell(spells, "heal"));
    }

    [Fact]
    public void IsMemorizingSpell_SpellAlreadyMemed_ReturnsFalse()
    {
        var spells = new[] { new MemorizedSpell(1, 3, "heal", Memed: true, Meming: false) };

        Assert.False(AutowalkRecoveryPolicy.IsMemorizingSpell(spells, "heal"));
    }

    [Fact]
    public void IsMemorizingSpell_SpellNotListedAtAll_ReturnsFalse()
    {
        Assert.False(AutowalkRecoveryPolicy.IsMemorizingSpell([], "heal"));
    }
    [Theory]
    [InlineData("resting", false)]
    [InlineData("RESTING", false)]
    [InlineData("standing", false)]
    [InlineData("sleeping", true)]
    [InlineData("SLEEPING", true)]
    [InlineData("sitting", true)]
    [InlineData(null, false)]
    public void RequiresStandBeforeMovement_DistinguishesRestFromSleep(string? position, bool expected)
    {
        Assert.Equal(expected, AutowalkRecoveryPolicy.RequiresStandBeforeMovement(position));
    }
}
