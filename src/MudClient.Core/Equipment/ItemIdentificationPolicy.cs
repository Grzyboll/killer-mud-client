using MudClient.Core.Gmcp;

namespace MudClient.Core.Equipment;

public enum IdentifySpellState
{
    Unavailable,
    KnownButNotMemorized,
    Ready
}

/// <summary>Combines confirmed spell knowledge with Char.MemSpell state for item identification.
/// A spell listed by Char.MemSpell is known even when it currently has no ready memorization.</summary>
public static class ItemIdentificationPolicy
{
    public static IdentifySpellState GetIdentifySpellState(bool isKnownFromSpellList, IReadOnlyList<MemorizedSpell> memorizedSpells)
    {
        var identifySlots = memorizedSpells.Where(spell => string.Equals(spell.Name, "identify", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (!isKnownFromSpellList && identifySlots.Length == 0)
        {
            return IdentifySpellState.Unavailable;
        }

        return identifySlots.Any(spell => spell.Memed && !spell.Meming)
            ? IdentifySpellState.Ready
            : IdentifySpellState.KnownButNotMemorized;
    }
}
