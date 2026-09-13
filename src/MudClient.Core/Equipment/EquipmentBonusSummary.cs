using System.Text.RegularExpressions;
using MudClient.Core.Text;
namespace MudClient.Core.Equipment;
public static partial class EquipmentBonusSummary
{
    [GeneratedRegex(@"^\[?(?:Wplywa na|Zmienia|Poprawia)\s+(?<name>.+?)\s+o\s+(?<value>-?\d+)\.?\]?$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex NumericBonus();
    [GeneratedRegex(@"^Dodaje\s+(?<name>.+?)\.$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex StaticEffect();
    [GeneratedRegex(@"^Typ broni:\s*'(?<type>[^']+)'\.$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex WeaponType();
    [GeneratedRegex(@"^Bonus do trafienia:\s*(?<bonus>-?\d+)\.$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex WeaponHitBonus();
    [GeneratedRegex(@"^Obrazenia zadawane\s+(?<damage>[^\r\n]+)\.$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex WeaponDamage();
    [GeneratedRegex(@"^Rodzaj pancerza:\s*(?<type>[^\r\n]+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex ArmorType();
    [GeneratedRegex(@"^Klasa pancerza:\s*(?<armor>[^\r\n]+)$", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)] private static partial Regex ArmorClass();
    public static IReadOnlyList<string> Summarize(IEnumerable<string> descriptions) => descriptions.Select(text => AnsiText.StripKillerColors(AnsiText.StripAnsi(text))).SelectMany(text => NumericBonus().Matches(text).Select(match => (Name: match.Groups["name"].Value.Trim(), Value: int.Parse(match.Groups["value"].Value)))).GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => GetCategory(group.Key).Order).ThenBy(group => GetCategory(group.Key).ItemOrder).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Select(group => $"{group.Key}: {group.Sum(item => item.Value):+#;-#;0}").ToList();
    public static IReadOnlyList<EquipmentBonusTotal> SummarizeWithSources(IEnumerable<(string Item, string Description)> entries) => entries.SelectMany(entry => NumericBonus().Matches(AnsiText.StripKillerColors(AnsiText.StripAnsi(entry.Description))).Select(match => new EquipmentBonusContribution(match.Groups["name"].Value.Trim(), int.Parse(match.Groups["value"].Value), entry.Item))).GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => GetCategory(group.Key).Order).ThenBy(group => GetCategory(group.Key).ItemOrder).ThenBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Select(group => new EquipmentBonusTotal(group.Key, group.Sum(item => item.Value), group.ToList(), GetCategory(group.Key).Label)).ToList();
    public static IReadOnlyList<EquipmentStaticEffectTotal> SummarizeStaticEffectsWithSources(IEnumerable<(string Item, string Description)> entries) => entries.SelectMany(entry => StaticEffect().Matches(AnsiText.StripKillerColors(AnsiText.StripAnsi(entry.Description))).Select(match => new EquipmentStaticEffectContribution(match.Groups["name"].Value.Trim(), entry.Item))).GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase).OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase).Select(group => new EquipmentStaticEffectTotal(group.Key, group.ToList())).ToList();

    /// <summary>Extracts only technical combat fields explicitly returned by <c>examine</c> for
    /// equipped weapon and shield slots. The server's slot label is the source of truth for a
    /// weapon's grip: <c>trzymane dwurecznie</c>, <c>pierwsza bron</c> or <c>druga bron</c>.</summary>
    public static IReadOnlyList<EquipmentCombatItem> SummarizeCombatItems(IEnumerable<(string Location, string Item, string Description)> entries)
    {
        var equipped = entries.Select(entry => new
        {
            entry.Location,
            entry.Item,
            Description = AnsiText.StripKillerColors(AnsiText.StripAnsi(entry.Description))
        }).ToList();
        return equipped.Where(entry => IsWeaponSlot(entry.Location) || IsShieldSlot(entry.Location))
            .Select(entry => CreateCombatItem(entry.Location, entry.Item, entry.Description))
            .ToList();
    }

    private static EquipmentCombatItem CreateCombatItem(string location, string item, string description)
    {
        if (IsWeaponSlot(location))
        {
            var type = WeaponType().Match(description).Groups["type"].Value.Trim();
            var hitBonus = WeaponHitBonus().Match(description).Groups["bonus"].Value.Trim();
            var damage = WeaponDamage().Match(description).Groups["damage"].Value.Trim();
            var stats = new List<string>();
            if (type.Length > 0) stats.Add($"typ: {type}");
            if (hitBonus.Length > 0) stats.Add($"trafienie: {int.Parse(hitBonus):+#;-#;0}");
            if (damage.Length > 0) stats.Add($"obrażenia: {damage}");
            return new EquipmentCombatItem("Broń", location, item, WeaponGrip(location), stats);
        }

        var armorType = ArmorType().Match(description).Groups["type"].Value.Trim();
        var armorClass = ArmorClass().Match(description).Groups["armor"].Value.Trim();
        var shieldStats = new List<string>();
        if (armorType.Length > 0) shieldStats.Add($"rodzaj: {armorType}");
        if (armorClass.Length > 0) shieldStats.Add($"klasa pancerza: {armorClass}");
        return new EquipmentCombatItem("Tarcza", location, item, null, shieldStats);
    }

    private static bool IsWeaponSlot(string location) =>
        location.Contains("bron", StringComparison.OrdinalIgnoreCase)
        || location.Contains("broń", StringComparison.OrdinalIgnoreCase)
        || location.Contains("trzymane dwurecznie", StringComparison.OrdinalIgnoreCase)
        || location.Contains("trzymane dwuręcznie", StringComparison.OrdinalIgnoreCase);
    private static bool IsShieldSlot(string location) => location.Contains("tarcza", StringComparison.OrdinalIgnoreCase);

    private static string WeaponGrip(string location)
    {
        if (location.Contains("trzymane dwurecznie", StringComparison.OrdinalIgnoreCase) || location.Contains("trzymane dwuręcznie", StringComparison.OrdinalIgnoreCase)) return "dwuręczna";
        if (location.Contains("pierwsza bron", StringComparison.OrdinalIgnoreCase) || location.Contains("pierwsza broń", StringComparison.OrdinalIgnoreCase)
            || location.Contains("druga bron", StringComparison.OrdinalIgnoreCase) || location.Contains("druga broń", StringComparison.OrdinalIgnoreCase)) return "jednoręczna";
        return "chwyt nieustalony";
    }

    private static BonusCategory GetCategory(string name)
    {
        var key = name.Trim().ToLowerInvariant();
        return key switch
        {
            "sile" or "sila" => new(0, 0, "Statystyki bazowe"),
            "zrecznosc" => new(0, 1, "Statystyki bazowe"),
            "kondycje" or "kondycja" => new(0, 2, "Statystyki bazowe"),
            "inteligencje" or "inteligencja" => new(0, 3, "Statystyki bazowe"),
            "wiedze" or "wiedza" => new(0, 4, "Statystyki bazowe"),
            "charyzme" or "charyzma" => new(0, 5, "Statystyki bazowe"),
            _ when key.Contains("zycia") || key.Contains("zycie") => new(1, 0, "Życie i ruch"),
            _ when key.Contains("ruchu") || key.Contains("ruch") => new(1, 1, "Życie i ruch"),
            _ when key.Contains("pancerz") => new(2, 0, "Klasa pancerza"),
            _ when key.Contains("obrazen") || key.Contains("obrażen") => new(3, 0, "Obrażenia i trafienie"),
            _ when key.Contains("trafien") || key.Contains("trafień") => new(3, 1, "Obrażenia i trafienie"),
            _ when key.Contains("odpornosci") || key.Contains("odporności") || key.Contains("odpornosc") || key.Contains("odporność") => new(4, 0, "Odporności"),
            _ when key.Contains("umiejetnos") || key.Contains("umiejętno") => new(6, 0, "Umiejętności"),
            _ => new(5, 0, "Pozostałe bonusy")
        };
    }

    private sealed record BonusCategory(int Order, int ItemOrder, string Label);
}
public sealed record EquipmentBonusContribution(string Name, int Value, string Item);
public sealed record EquipmentBonusTotal(string Name, int Value, IReadOnlyList<EquipmentBonusContribution> Contributions, string Category);
public sealed record EquipmentStaticEffectContribution(string Name, string Item);
public sealed record EquipmentStaticEffectTotal(string Name, IReadOnlyList<EquipmentStaticEffectContribution> Contributions);
public sealed record EquipmentCombatItem(string Category, string Location, string Item, string? Grip, IReadOnlyList<string> Stats);
