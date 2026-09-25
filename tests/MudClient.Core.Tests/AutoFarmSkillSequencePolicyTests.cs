using MudClient.Core.Automation;

namespace MudClient.Core.Tests;

public sealed class AutoFarmSkillSequencePolicyTests
{
    private static readonly Dictionary<string, bool> NoCooldowns = new(StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> NoActiveAffects = new(StringComparer.OrdinalIgnoreCase);

    private static AutoFarmSkill SelfSkill(string name) => new(name, Offensive: false);

    private static AutoFarmSkill Offensive(string name) => new(name, Offensive: true);

    [Fact]
    public void GetSkillsNeedingUse_SkipsSkillOnCooldown()
    {
        var cooldowns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["kick"] = true };

        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick"), Offensive("bash")], NoActiveAffects, cooldowns);

        Assert.Equal([Offensive("bash")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_NotReportedByTimeouts_IsReady()
    {
        // Char.Skills.Timeout only ever reports skills currently on cooldown — absence means ready.
        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick")], NoActiveAffects, NoCooldowns);

        Assert.Equal([Offensive("kick")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_SkipsAlreadyActiveSelfSkill()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "berserk" };

        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [SelfSkill("berserk"), SelfSkill("second wind")], active, NoCooldowns);

        Assert.Equal([SelfSkill("second wind")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_OffensiveEntry_IsNeverSkippedForBeingActive()
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kick" };

        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick")], active, NoCooldowns);

        Assert.Equal([Offensive("kick")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_PreservesTheConfiguredOrder()
    {
        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("bash"), Offensive("kick")], NoActiveAffects, NoCooldowns);

        Assert.Equal([Offensive("bash"), Offensive("kick")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_MixOfSelfAndOffensive_BothReturnedInOrder()
    {
        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [SelfSkill("berserk"), Offensive("kick")], NoActiveAffects, NoCooldowns);

        Assert.Equal([SelfSkill("berserk"), Offensive("kick")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_WithinMinInterval_IsSkipped()
    {
        var now = DateTimeOffset.UtcNow;
        var lastUsedAt = new Dictionary<string, DateTimeOffset> { ["kick"] = now.AddSeconds(-1) };

        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick")], NoActiveAffects, NoCooldowns, now, lastUsedAt);

        Assert.Empty(result);
    }

    [Fact]
    public void GetSkillsNeedingUse_PastMinInterval_IsReturned()
    {
        var now = DateTimeOffset.UtcNow;
        var lastUsedAt = new Dictionary<string, DateTimeOffset>
        {
            ["kick"] = now - AutoFarmSkillSequencePolicy.MinSkillUseInterval,
        };

        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick")], NoActiveAffects, NoCooldowns, now, lastUsedAt);

        Assert.Equal([Offensive("kick")], result);
    }

    [Fact]
    public void GetSkillsNeedingUse_NoUseHistoryTracked_MinIntervalNotApplied()
    {
        var result = AutoFarmSkillSequencePolicy.GetSkillsNeedingUse(
            [Offensive("kick")], NoActiveAffects, NoCooldowns);

        Assert.Equal([Offensive("kick")], result);
    }
}
