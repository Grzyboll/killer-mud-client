using System.Text.RegularExpressions;
using MudClient.Core.Text;

namespace MudClient.Core.Automation;

/// <summary>
/// Recognizes the MUD's "Zdobyłeś &lt;N&gt; punktów doświadczenia." line (a kill just landed) —
/// the signal "/autoget" (Auto: Ekwipunek) uses to loot the fresh corpse automatically instead of
/// needing a manual "/autoget" after every fight.
/// </summary>
public static class ExperienceGainPolicy
{
    // "punkt\w*" covers every Polish declension the amount can trigger (1 punkt, 2-4 punkty,
    // 5+/0 punktow) without enumerating them — folded, so "punktow" matches "punktów" too.
    private static readonly Regex Pattern = new(
        @"\bzdobyles\s+\d+\s+punkt\w*\s+doswiadczenia\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool IsExperienceGainLine(string line) =>
        Pattern.IsMatch(PolishText.Fold(line));
}
