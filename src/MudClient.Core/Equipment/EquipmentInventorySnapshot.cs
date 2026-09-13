using System.Text.RegularExpressions;
using MudClient.Core.Text;

namespace MudClient.Core.Equipment;

public sealed record EquipmentItem(string Location, string Name);
public sealed record InventoryItem(string Name);
public sealed record EquipmentInventorySnapshot(IReadOnlyList<EquipmentItem> Equipment, IReadOnlyList<InventoryItem> Inventory);
public enum InventoryMutationKind { Added, Removed, PutIntoContainer, TakenFromContainer }
public sealed record ItemCommandReference(string Word, int Occurrence)
{
    public string Argument => Occurrence <= 1 ? Word : $"{Occurrence}.{Word}";
}

/// <summary>Parses the text tables returned by the MUD's <c>eq</c> and <c>inv</c> commands.
/// A response is accepted only after its observed heading; unknown lines are never made into items.</summary>
public static partial class EquipmentInventorySnapshotParser
{
    [GeneratedRegex("^\\s*<(?<slot>[^>]+)>\\s*(?<name>.+?)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EquipmentLine();
    [GeneratedRegex("^<\\d+/\\d+hp", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PromptLine();
    [GeneratedRegex(@"\((?<percent>\d{1,3})%\)", RegexOptions.CultureInvariant)]
    private static partial Regex Durability();

    public static bool TryParseEquipment(string response, out IReadOnlyList<EquipmentItem> items)
    {
        var heading = FindHeading(response, "uzywasz:");
        if (heading < 0) { items = []; return false; }
        var result = new List<EquipmentItem>();
        foreach (var raw in response.Split('\n').Skip(heading + 1))
        {
            var line = AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim();
            if (PromptLine().IsMatch(line)) break;
            var match = EquipmentLine().Match(line);
            if (match.Success)
            {
                var closingBracket = raw.IndexOf('>');
                result.Add(new EquipmentItem(match.Groups["slot"].Value.Trim(), closingBracket >= 0 ? raw[(closingBracket + 1)..].Trim() : match.Groups["name"].Value.Trim()));
            }
        }
        items = result;
        return true;
    }

    /// <summary>Inventory output has no trusted per-item decorations in captured source. We retain
    /// only non-empty, non-prompt lines after the confirmed heading verbatim.</summary>
    public static bool TryParseInventory(string response, out IReadOnlyList<InventoryItem> items)
    {
        var heading = FindHeading(response, "nosisz przy sobie:");
        if (heading < 0) { items = []; return false; }
        var result = new List<InventoryItem>();
        foreach (var raw in response.Split('\n').Skip(heading + 1))
        {
            var line = AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim();
            if (PromptLine().IsMatch(line)) break;
            if (line.Length != 0) result.Add(new InventoryItem(raw.Trim()));
        }
        items = result;
        return true;
    }

    public static string BuildExamineCommand(string itemName, int occurrence)
    {
        var firstWord = BuildItemCommandTarget(itemName);
        return occurrence <= 1 ? $"examine {firstWord}" : $"examine {occurrence}.{firstWord}";
    }

    /// <summary>Returns the first actual item-name word, excluding MUD visual annotations such as
    /// "(pulsuje)" and durability. It is suitable only as an editable starting point for a player
    /// command; only <see cref="BuildExamineCommand"/> has confirmed duplicate-numbering rules.</summary>
    public static string BuildItemCommandTarget(string itemName) => FirstWord(itemName);

    /// <summary>Returns the complete item name suitable for inserting into the editable command
    /// line. Terminal colour sequences and parenthetical visual annotations are excluded.</summary>
    public static string GetPlainItemName(string itemName) => string.Join(' ', Words(itemName));

    /// <summary>Chooses the least ambiguous word from an item name against the server's combined
    /// inventory-then-equipment lookup list. The MUD was observed to treat a command word as a
    /// prefix, therefore "krysztal" also matches "krysztalowy". One- and two-character connector
    /// words are ignored while a longer item-name word exists.</summary>
    public static ItemCommandReference ResolveItemCommandReference(EquipmentInventorySnapshot snapshot, string itemName, bool inInventory, int index)
    {
        var ordered = snapshot.Inventory.Select(item => item.Name)
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var targetOffset = inInventory ? index : snapshot.Inventory.Count + index;
        var candidates = Words(itemName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var meaningfulCandidates = candidates.Where(word => word.Length >= 3).ToList();
        if (meaningfulCandidates.Count > 0)
        {
            candidates = meaningfulCandidates;
        }

        var selected = candidates
            .Select((word, position) => new
            {
                Word = word,
                Position = position,
                Matches = ordered.Count(name => MatchesCommandWord(name, word))
            })
            .OrderBy(candidate => candidate.Matches)
            .ThenBy(candidate => candidate.Position)
            .FirstOrDefault();

        if (selected is null)
        {
            return new ItemCommandReference(string.Empty, 1);
        }

        var occurrence = ordered.Take(targetOffset + 1).Count(name => MatchesCommandWord(name, selected.Word));
        return new ItemCommandReference(selected.Word, occurrence);
    }

    public static string BuildItemCommand(string verb, ItemCommandReference reference) =>
        string.IsNullOrWhiteSpace(reference.Word) ? verb : $"{verb} {reference.Argument}";

    /// <summary>Occurrence numbering follows the server order: inventory first, then equipment.</summary>
    public static int GetExamineOccurrence(EquipmentInventorySnapshot snapshot, string itemName, bool inInventory, int index)
    {
        var word = FirstWord(itemName);
        var ordered = snapshot.Inventory.Select(item => item.Name)
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var targetOffset = inInventory ? index : snapshot.Inventory.Count + index;
        return ordered.Take(targetOffset + 1).Count(name => Words(name)
            .Any(candidate => candidate.StartsWith(word, StringComparison.OrdinalIgnoreCase)));
    }

    public static bool ContainsPrompt(string text) => text.Split('\n').Any(line =>
        PromptLine().IsMatch(AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).TrimStart()));

    public static int? GetDurabilityPercent(string itemName)
    {
        var match = Durability().Match(AnsiText.StripKillerColors(AnsiText.StripAnsi(itemName)));
        return match.Success ? int.Parse(match.Groups["percent"].Value) : null;
    }

    public static string WithoutDurabilityPercent(string itemName) =>
        Durability().Replace(AnsiText.StripKillerColors(AnsiText.StripAnsi(itemName)), string.Empty).Trim();

    /// <summary>Recognizes the observed container section in an <c>examine</c> response. Only
    /// lines after "&lt;container&gt; (...) zawiera:" and before the prompt are returned; the
    /// descriptive paragraph remains outside the container data.</summary>
    public static bool TryParseContainerContents(string response, string itemName, out IReadOnlyList<InventoryItem> contents)
    {
        var expectedName = GetPlainItemName(itemName);
        var lines = response.Split('\n');
        var headerIndex = Array.FindIndex(lines, line =>
        {
            var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line));
            plain = Regex.Replace(plain, @"\([^)]*\)", " ").Trim();
            const string suffix = " zawiera:";
            return plain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                && string.Equals(plain[..^suffix.Length].Trim(), expectedName, StringComparison.OrdinalIgnoreCase);
        });
        if (headerIndex < 0)
        {
            contents = [];
            return false;
        }

        var result = new List<InventoryItem>();
        foreach (var raw in lines.Skip(headerIndex + 1))
        {
            var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim();
            if (PromptLine().IsMatch(plain)) break;
            if (plain.Length > 0) result.Add(new InventoryItem(raw.Trim()));
        }
        contents = result;
        return true;
    }

    /// <summary>Recognizes only inventory mutations observed in server messages. A matching
    /// message means the top-level <c>inv</c> listing is stale, including when an item moved into
    /// or out of a carried container.</summary>
    public static bool IsInventoryMutationMessage(string line)
    {
        return GetInventoryMutationKind(line) is not null;
    }

    /// <summary>Classifies only the successful inventory mutations observed in game logs.</summary>
    public static InventoryMutationKind? GetInventoryMutationKind(string line)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).TrimStart();
        if (plain.StartsWith("Podnosisz ", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Kupujesz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.Added;
        if (plain.StartsWith("Upuszczasz ", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Sprzedajesz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.Removed;
        if (plain.StartsWith("Wkladasz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.PutIntoContainer;
        if (plain.StartsWith("Wyjmujesz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.TakenFromContainer;
        return null;
    }

    /// <summary>Prepares a tooltip from server data: CRLF is one line break, blank source lines
    /// remain blank and the terminal prompt is excluded. When the response begins with a flavour
    /// description, lines before the first one that starts with the full item name are omitted.</summary>
    public static string WithoutPromptForTooltip(string text, string? itemName = null)
    {
        var lines = AnsiText.StripKillerColors(AnsiText.StripAnsi(text)).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var content = lines.TakeWhile(line => !PromptLine().IsMatch(line.TrimStart())).ToArray();
        var normalizedItemName = NormalizeItemName(itemName);
        if (normalizedItemName.Length > 0)
        {
            var technicalStart = Array.FindIndex(content, line => NormalizeItemName(line).StartsWith(normalizedItemName, StringComparison.OrdinalIgnoreCase));
            if (technicalStart > 0)
            {
                content = content[technicalStart..];
            }
        }
        return string.Join("\n", content).TrimEnd('\n');
    }


    private static int FindHeading(string text, string heading)
    {
        var lines = text.Split('\n');
        for (var index = 0; index < lines.Length; index++)
            if (string.Equals(AnsiText.StripKillerColors(AnsiText.StripAnsi(lines[index])).Trim(), heading, StringComparison.OrdinalIgnoreCase)) return index;
        return -1;
    }
    private static string FirstWord(string text)
    {
        return Words(text).FirstOrDefault() ?? string.Empty;
    }
    private static IReadOnlyList<string> Words(string text)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(text));
        // Parenthetical prefixes/suffixes are visual state (e.g. "(pulsuje)", durability,
        // "pod rękawicami"), not an item-name token accepted by examine.
        plain = Regex.Replace(plain, @"\([^)]*\)", " ");
        return plain.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static bool MatchesCommandWord(string itemName, string commandWord) =>
        Words(itemName).Any(candidate => candidate.StartsWith(commandWord, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeItemName(string? text) =>
        string.Join(' ', Words(text ?? string.Empty));
}
