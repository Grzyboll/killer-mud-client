using MudClient.Core.Automation;
using MudClient.Core.Gmcp;

namespace MudClient.Core.Tests;

public sealed class HealthRecoveryPolicyTests
{
    [Fact]
    public void IsBelowThreshold_AtThreshold_ReturnsTrue()
    {
        Assert.True(HealthRecoveryPolicy.IsBelowThreshold(30, 100, 30));
    }

    [Fact]
    public void IsBelowThreshold_AboveThreshold_ReturnsFalse()
    {
        Assert.False(HealthRecoveryPolicy.IsBelowThreshold(31, 100, 30));
    }

    [Theory]
    [InlineData(null, 100)]
    [InlineData(30, null)]
    [InlineData(30, 0)]
    public void IsBelowThreshold_MissingOrInvalidData_ReturnsFalse(int? hp, int? maxHp)
    {
        Assert.False(HealthRecoveryPolicy.IsBelowThreshold(hp, maxHp, 30));
    }

    [Fact]
    public void GetRecoveryAction_EmptyHealSpellList_AlwaysRests()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "heal", Memed: true, Meming: false) };

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.Rest, null),
            HealthRecoveryPolicy.GetRecoveryAction([], spells));
        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.Rest, null),
            HealthRecoveryPolicy.GetRecoveryAction(["", "   "], spells));
    }

    [Fact]
    public void GetRecoveryAction_HealSpellMemorized_CastsIt()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "heal", Memed: true, Meming: false) };

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.CastHeal, "heal"),
            HealthRecoveryPolicy.GetRecoveryAction(["heal"], spells));
    }

    [Fact]
    public void GetRecoveryAction_HealSpellNotMemorizedOrMeming_MemorizesIt()
    {
        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.MemorizeHeal, "heal"),
            HealthRecoveryPolicy.GetRecoveryAction(["heal"], []));
    }

    [Fact]
    public void GetRecoveryAction_HealSpellAlreadyBeingMemorized_JustRests()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "heal", Memed: false, Meming: true) };

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.Rest, "heal"),
            HealthRecoveryPolicy.GetRecoveryAction(["heal"], spells));
    }

    [Fact]
    public void GetRecoveryAction_IsCaseInsensitiveOnSpellName()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "Heal", Memed: true, Meming: false) };

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.CastHeal, "heal"),
            HealthRecoveryPolicy.GetRecoveryAction(["heal"], spells));
    }

    [Fact]
    public void GetRecoveryAction_StrongestMemorized_CastsStrongestNotFirstMemorized()
    {
        // "cure light" (weakest, listed last) is memorized; "cure critical" (strongest, listed
        // first) is not — the strongest MEMORIZED one should still win over a weaker one that
        // merely happens to be ready, since the whole point of the list is priority-by-strength.
        var spells = new[]
        {
            new MemorizedSpell(1, 1, "cure serious", Memed: true, Meming: false),
            new MemorizedSpell(2, 1, "cure light", Memed: true, Meming: false),
        };

        var decision = HealthRecoveryPolicy.GetRecoveryAction(
            ["cure critical", "cure serious", "cure light"], spells);

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.CastHeal, "cure serious"), decision);
    }

    [Fact]
    public void GetRecoveryAction_NoneMemorized_MemorizesStrongestNotAlreadyMeming()
    {
        var decision = HealthRecoveryPolicy.GetRecoveryAction(
            ["cure critical", "cure serious"], []);

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.MemorizeHeal, "cure critical"), decision);
    }

    [Fact]
    public void GetRecoveryAction_StrongestAlreadyMeming_RestsInsteadOfMemorizingAWeakerOne()
    {
        // Only one "mem" queue slot is realistically ever in flight — if the strongest candidate
        // is already being memorized, wait for it rather than kicking off a second, weaker mem.
        var spells = new[] { new MemorizedSpell(1, 1, "cure critical", Memed: false, Meming: true) };

        var decision = HealthRecoveryPolicy.GetRecoveryAction(
            ["cure critical", "cure serious"], spells);

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.Rest, "cure critical"), decision);
    }

    [Fact]
    public void GetRecoveryAction_BlankEntriesInListAreIgnoredForPriority()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "cure serious", Memed: true, Meming: false) };

        var decision = HealthRecoveryPolicy.GetRecoveryAction(
            ["", "cure serious", "   "], spells);

        Assert.Equal(new HealthRecoveryDecision(HealthRecoveryAction.CastHeal, "cure serious"), decision);
    }

    [Fact]
    public void GetSpellsNeedingMemorization_MemorizedSpell_IsNotReturned()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "armor", Memed: true, Meming: false) };

        Assert.Empty(HealthRecoveryPolicy.GetSpellsNeedingMemorization(["armor"], spells));
    }

    [Fact]
    public void GetSpellsNeedingMemorization_AlreadyBeingMemorized_IsNotReturned()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "armor", Memed: false, Meming: true) };

        Assert.Empty(HealthRecoveryPolicy.GetSpellsNeedingMemorization(["armor"], spells));
    }

    [Fact]
    public void GetSpellsNeedingMemorization_NeitherMemedNorMeming_IsReturned()
    {
        Assert.Equal(["armor"], HealthRecoveryPolicy.GetSpellsNeedingMemorization(["armor"], []));
    }

    [Fact]
    public void GetSpellsNeedingMemorization_MixOfSatisfiedAndMissing_ReturnsOnlyMissing()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "armor", Memed: true, Meming: false) };

        var missing = HealthRecoveryPolicy.GetSpellsNeedingMemorization(["armor", "bless"], spells);

        Assert.Equal(["bless"], missing);
    }

    [Fact]
    public void GetSpellsNeedingMemorization_BlankEntriesAreIgnored()
    {
        Assert.Empty(HealthRecoveryPolicy.GetSpellsNeedingMemorization(["", "   "], []));
    }

    [Fact]
    public void GetSpellsNeedingMemorization_IsCaseInsensitiveOnSpellName()
    {
        var spells = new[] { new MemorizedSpell(1, 1, "Armor", Memed: true, Meming: false) };

        Assert.Empty(HealthRecoveryPolicy.GetSpellsNeedingMemorization(["armor"], spells));
    }

    private static readonly MemorizedSpell[] HealMemorized =
        [new MemorizedSpell(1, 1, "heal", Memed: true, Meming: false)];
    private static readonly Dictionary<string, bool> NoTimeouts = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void ShouldCastCombatHeal_BelowThresholdMemorizedAndOffCooldown_ReturnsTrue()
    {
        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts);

        Assert.Equal((true, "heal"), result);
    }

    [Fact]
    public void ShouldCastCombatHeal_NotEnabled_ReturnsFalse()
    {
        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: false, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_AboveThreshold_ReturnsFalse()
    {
        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 80, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_HealSpellNotMemorizedYet_ReturnsFalse()
    {
        // Mid-combat there's no point latching onto MemorizeHeal/Rest — those need the
        // room-arrival flow, which can actually "mem"/"rest" outside of a fight.
        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: [], skillTimeouts: NoTimeouts);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_HealSpellAlreadyOnCooldown_ReturnsFalse()
    {
        var timeouts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["heal"] = true };

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: timeouts);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_HealSpellCooldownClearedAgain_ReturnsTrue()
    {
        var timeouts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["heal"] = false };

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: timeouts);

        Assert.Equal((true, "heal"), result);
    }

    [Fact]
    public void ShouldCastCombatHeal_SpellNeverSeenInTimeoutTracking_TreatedAsOffCooldown()
    {
        var timeouts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["other spell"] = true };

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: timeouts);

        Assert.Equal((true, "heal"), result);
    }

    [Fact]
    public void ShouldCastCombatHeal_IsCaseInsensitiveOnSpellNameForTimeoutLookup()
    {
        var timeouts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase) { ["Heal"] = true };

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: timeouts);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_WeakerSpellMemorizedButStrongerIsOnCooldown_DoesNotFallBackToWeaker()
    {
        // Mirrors GetRecoveryAction's priority: mid-combat, ShouldCastCombatHeal only ever
        // considers the SAME spell GetRecoveryAction would pick (the strongest memorized one) —
        // it must not fall back to a weaker memorized spell just because the top choice happens
        // to be on cooldown right now.
        var spells = new[]
        {
            new MemorizedSpell(1, 1, "cure critical", Memed: true, Meming: false),
            new MemorizedSpell(2, 1, "cure light", Memed: true, Meming: false),
        };
        var timeouts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["cure critical"] = true,
        };

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["cure critical", "cure light"], memorizedSpells: spells, skillTimeouts: timeouts);

        Assert.False(result.ShouldCast);
        Assert.Null(result.SpellName);
    }

    [Fact]
    public void ShouldCastCombatHeal_LastCastTooRecent_ReturnsFalseEvenWhenOffCooldown()
    {
        // Regression target: Char.Skills.Timeout can lag behind Char.Vitals ticks, so relying on
        // skillTimeouts alone let several vitals ticks in that gap each re-approve a cast before
        // the first one's own cooldown was ever reported — burning through the whole priority
        // list's memorization charges in a few seconds. now/lastCastAt back that up.
        var now = DateTimeOffset.UtcNow;
        var lastCastAt = now - TimeSpan.FromSeconds(1);

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts,
            now: now, lastCastAt: lastCastAt);

        Assert.False(result.ShouldCast);
    }

    [Fact]
    public void ShouldCastCombatHeal_LastCastPastTheMinInterval_ReturnsTrue()
    {
        var now = DateTimeOffset.UtcNow;
        var lastCastAt = now - HealthRecoveryPolicy.MinCombatHealCastInterval - TimeSpan.FromMilliseconds(1);

        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts,
            now: now, lastCastAt: lastCastAt);

        Assert.Equal((true, "heal"), result);
    }

    [Fact]
    public void ShouldCastCombatHeal_NoCastHistoryProvided_SkipsTheIntervalCheck()
    {
        // Callers that don't track cast history (existing tests above, or any future caller) get
        // the original skillTimeouts-only behavior — the interval check only ever engages when
        // both now and lastCastAt are supplied.
        var result = HealthRecoveryPolicy.ShouldCastCombatHeal(
            enabled: true, hp: 30, maxHp: 100, thresholdPercent: 50,
            healSpellNames: ["heal"], memorizedSpells: HealMemorized, skillTimeouts: NoTimeouts,
            now: DateTimeOffset.UtcNow, lastCastAt: null);

        Assert.Equal((true, "heal"), result);
    }
}
