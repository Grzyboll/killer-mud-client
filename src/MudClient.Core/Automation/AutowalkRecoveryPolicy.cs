using System.Globalization;
using System.Text;
using MudClient.Core.Gmcp;

namespace MudClient.Core.Automation;

public enum LowMovementAction
{
    None,
    CastRefresh,
    Rest,
}

/// <summary>Pure autowalk recovery decisions based on the latest GMCP state and MUD lines.</summary>
public static class AutowalkRecoveryPolicy
{
    /// <summary>Commands tried in order when autowalk encounters a locked gate.</summary>
    public static IReadOnlyList<string> GetGateOpeningCommands() =>
        ["zapukaj", "pull", "pociagnij", "uderz"];

    public static LowMovementAction GetLowMovementAction(
        int? movement,
        int? maximumMovement,
        IReadOnlyList<MemorizedSpell> memorizedSpells,
        int thresholdPercent = 10)
    {
        if (movement is null || maximumMovement is null || maximumMovement <= 0 ||
            (long)movement.Value * 100 > (long)maximumMovement.Value * thresholdPercent)
        {
            return LowMovementAction.None;
        }

        return HasMemorizedSpell(memorizedSpells, "refresh")
            ? LowMovementAction.CastRefresh
            : LowMovementAction.Rest;
    }

    /// <summary>True when the GMCP position reports the character is in combat.</summary>
    public static bool IsCombatPosition(string? position) =>
        string.Equals(position, "fighting", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when GMCP reports that movement requires standing up first.</summary>
    public static bool IsSittingPosition(string? position) =>
        string.Equals(position, "sitting", StringComparison.OrdinalIgnoreCase);

    /// <summary>KillerMUD allows movement while resting. Sitting and sleeping require stand.
    /// Timed low-movement recovery and an explicit mid-route rest keep their own pause lifecycle.</summary>
    public static bool RequiresStandBeforeMovement(string? position) =>
        string.Equals(position, "sleeping", StringComparison.OrdinalIgnoreCase) ||
        IsSittingPosition(position);

    /// <summary>True when GMCP confirms that the character can resume walking.</summary>
    public static bool IsStandingPosition(string? position) =>
        string.Equals(position, "standing", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when GMCP reports the character is resting — a distinct position from
    /// "sitting" (the "rest" command, not "sit").</summary>
    public static bool IsRestingPosition(string? position) =>
        string.Equals(position, "resting", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the spell is memorized and ready to cast.</summary>
    public static bool HasMemorizedSpell(IReadOnlyList<MemorizedSpell> memorizedSpells, string name) =>
        memorizedSpells.Any(spell =>
            spell.Memed &&
            string.Equals(spell.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when the spell is currently being memorized (not yet ready to cast) — used
    /// to avoid re-issuing a "mem" command while one is already in flight.</summary>
    public static bool IsMemorizingSpell(IReadOnlyList<MemorizedSpell> memorizedSpells, string name) =>
        memorizedSpells.Any(spell =>
            spell.Meming &&
            string.Equals(spell.Name.Trim(), name, StringComparison.OrdinalIgnoreCase));

    public static bool IsLockedGateMessage(string line)
    {
        var normalized = RemoveDiacritics(line).Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.Contains("brama jest zamknieta na klucz", StringComparison.Ordinal) ||
               normalized.Contains("brama jest zamknieta", StringComparison.Ordinal);
    }

    private static string RemoveDiacritics(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                result.Append(character);
            }
        }

        return result.ToString().Normalize(NormalizationForm.FormC);
    }
}
