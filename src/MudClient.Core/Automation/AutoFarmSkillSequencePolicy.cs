namespace MudClient.Core.Automation;

/// <summary>
/// Pure decisions for auto-farm's "use these the moment combat starts, in this order" skill
/// sequence — the skill counterpart of <see cref="AutoFarmCastSequencePolicy"/>. No memorization step
/// exists for skills, so unlike the spell version this is the only policy method the feature needs:
/// readiness is entirely "off cooldown" (<see cref="Gmcp.SkillTimeoutResolver"/>'s Char.Skills.Timeout
/// data) plus, for a self skill, "not already an active affect".
/// </summary>
public static class AutoFarmSkillSequencePolicy
{
    /// <summary>Client-side floor between two uses of the same skill, mirroring
    /// <see cref="HealthRecoveryPolicy.MinCombatHealCastInterval"/>'s own xmldoc for why: it covers
    /// the gap between firing a skill and the server's own Char.Skills.Timeout report catching up,
    /// so a caller re-checking on every Char.Vitals tick can't fire the same skill twice in a row
    /// before the cooldown is actually reported.</summary>
    public static readonly TimeSpan MinSkillUseInterval = TimeSpan.FromSeconds(2);

    /// <summary>Sequence entries, in the user's own defined order, that are off cooldown and ready
    /// to use right now: a self entry only once it isn't already an active affect, an offensive
    /// entry unconditionally (there's nothing to check an attack skill against) once off cooldown.
    /// <paramref name="activeAffectNames"/> is expected already normalized/case-insensitive the same
    /// way the buffs panel's own live Char.Affects tracking is; <paramref name="skillTimeouts"/> is
    /// <see cref="Gmcp.SkillTimeoutResolver"/>'s Char.Skills.Timeout snapshot — a name absent from it
    /// is ready (only skills currently on cooldown are ever reported). <paramref name="now"/>/
    /// <paramref name="lastUsedAt"/> are optional so a caller that doesn't track use history keeps
    /// working unchanged, just without <see cref="MinSkillUseInterval"/>'s extra floor.</summary>
    public static IReadOnlyList<AutoFarmSkill> GetSkillsNeedingUse(
        IReadOnlyList<AutoFarmSkill> skills,
        IReadOnlySet<string> activeAffectNames,
        IReadOnlyDictionary<string, bool> skillTimeouts,
        DateTimeOffset? now = null,
        IReadOnlyDictionary<string, DateTimeOffset>? lastUsedAt = null) =>
        skills
            .Where(skill => !string.IsNullOrWhiteSpace(skill.Name))
            .Where(skill => !(skillTimeouts.TryGetValue(skill.Name, out var onCooldown) && onCooldown))
            .Where(skill => skill.Offensive || !activeAffectNames.Contains(skill.Name.Trim()))
            .Where(skill =>
                now is not { } currentTime ||
                lastUsedAt is null ||
                !lastUsedAt.TryGetValue(skill.Name, out var previousUsedAt) ||
                currentTime - previousUsedAt >= MinSkillUseInterval)
            .ToArray();
}
