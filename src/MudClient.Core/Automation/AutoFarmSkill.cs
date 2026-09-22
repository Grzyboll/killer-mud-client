namespace MudClient.Core.Automation;

/// <summary>One entry in auto-farm's "use on combat start" skill sequence (see
/// <see cref="AutoFarmSkillSequencePolicy"/>) — the skill counterpart of <see cref="AutoFarmCastSpell"/>
/// for characters who fight with combat skills (kick, bash, ...) instead of, or alongside, spells.
/// Unlike a spell, a skill never needs memorization: readiness is entirely a cooldown question, read
/// from the same Char.Skills.Timeout data <see cref="Gmcp.SkillTimeoutResolver"/> already tracks. A
/// self skill (<c>Offensive: false</c>, e.g. a self-buff like "berserk") is used on self and skipped
/// once already an active affect; an offensive skill (<c>Offensive: true</c>, e.g. "kick") is aimed at
/// whichever mob the character is currently fighting instead, and always fires once off cooldown —
/// there's no "already active" state to check for an attack skill.</summary>
public sealed record AutoFarmSkill(string Name, bool Offensive);
