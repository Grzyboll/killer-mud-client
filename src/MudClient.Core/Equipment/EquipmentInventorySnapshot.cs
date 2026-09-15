using System.Text.RegularExpressions;
using MudClient.Core.Text;

namespace MudClient.Core.Equipment;

public sealed record EquipmentItem(string Location, string Name);
public sealed record InventoryItem(string Name);
public sealed record EquipmentInventorySnapshot(IReadOnlyList<EquipmentItem> Equipment, IReadOnlyList<InventoryItem> Inventory, IReadOnlyList<InventoryItem>? Ground = null)
{
    public IReadOnlyList<InventoryItem> GroundItems => Ground ?? [];
}
public enum InventoryMutationKind { Added, Removed, PutIntoContainer, TakenFromContainer }

/// <summary>Observed accessibility state of a confirmed container.  The client only sets a
/// value after receiving the corresponding server response.</summary>
public enum ContainerAccessState { None, Open, Closed, Locked, MissingKey }
public enum ContainerOperationOutcome { Opened, Unlocked, Closed, Locked }
public sealed record ItemCommandReference(string Word, int Occurrence)
{
    public string Argument => Occurrence <= 1 ? Word : $"{Occurrence}.{Word}";
}

/// <summary>Parses the text tables returned by the MUD's <c>eq</c> and <c>inv</c> commands.
/// A response is accepted only after its observed heading; unknown lines are never made into items.</summary>
public static partial class EquipmentInventorySnapshotParser
{
    // These words only qualify a room object for one examine. They are deliberately not evidence
    // that it is a container; the server's "zawiera:" response remains the sole confirmation.
    private static readonly HashSet<string> PotentialGroundContainerWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "cialo", "trup", "zwloki", "szczatki", "kufer", "skrzynia", "skrzynka", "szafka",
        "regał", "regal", "komoda", "kredens", "sejf", "skrzyn", "beczka", "baryłka", "barylka",
        "worek", "torba", "plecak", "sakiewka", "mieszek", "paczka", "pakunek", "kosz",
        "koszyk", "skrzynka", "pudlo", "pudełko", "pudelko", "skrytka", "schowek", "szafa",
        "gablotka", "gablota", "skrzynia", "trumna", "sarkofag", "urna", "amfora",
        "kuferek", "pojemnik", "naczynie", "skrzynia", "lada", "skład", "sklad",
        "spiżarnia", "spizarnia", "skarbiec", "szkatułka", "szkatulka", "kaseta", "stojak", "skrzyn"
    };
    [GeneratedRegex("^\\s*<(?<slot>[^>]+)>\\s*(?<name>.+?)\\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex EquipmentLine();
    [GeneratedRegex("^<\\d+/\\d+hp", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PromptLine();
    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    private static partial Regex LetterWords();
    [GeneratedRegex(@"[^\p{L}\p{N}\s]+", RegexOptions.CultureInvariant)]
    private static partial Regex ItemNamePunctuation();
    [GeneratedRegex(@"\((?<percent>\d{1,3})%\)", RegexOptions.CultureInvariant)]
    private static partial Regex Durability();
    [GeneratedRegex("^\\s*(?<name>.+?)\\s+(?<verb>lezy|leza|wala|walaja|stoi)(?:\\s+sie)?\\s+(?:tu|tutaj)(?<tail>\\s+.*)?\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundItemLine();
    [GeneratedRegex("^\\s*(?:lezy|leza)\\s+tu\\s+(?<name>.+?)\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundItemReverseLine();
    [GeneratedRegex("^\\s*Widzisz\\s+(?<name>.+?)\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundSeenItemLine();
    [GeneratedRegex("^\\s*(?<name>.+?)\\s+przyciagaja\\s+twoj\\s+wzrok\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundAttentionItemLine();
    [GeneratedRegex("^\\s*(?<name>.+?)\\s+plywa\\s+sobie\\s+tutaj\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundFloatingItemLine();
    [GeneratedRegex("^\\s*(?<name>.+?\\s+wykonan[yae]\\s+z\\s+.+?)\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundMadeItemLine();
    [GeneratedRegex("^\\s*.+?\\s+znajduje\\s+sie\\s+(?<name>.+?)\\.\\s*$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex GroundLocatedItemLine();

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

    /// <summary>Reads the item block after the room description. In observed <c>look</c> output,
    /// every non-empty line in that block is a ground object unless it names a GMCP person.
    /// Specific sentence forms are reduced to just their item name where known.</summary>
    public static IReadOnlyList<InventoryItem> ParseGroundItems(string response, IReadOnlyCollection<string> roomPersonNames)
    {
        var people = roomPersonNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<InventoryItem>();
        var lines = response.Split('\n');
        var blocks = new List<List<string>>();
        var currentBlock = new List<string>();
        foreach (var raw in lines)
        {
            var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim();
            if (PromptLine().IsMatch(plain)) break;
            if (plain.Length == 0)
            {
                if (currentBlock.Count > 0)
                {
                    blocks.Add(currentBlock);
                    currentBlock = [];
                }
                continue;
            }
            currentBlock.Add(plain);
        }
        if (currentBlock.Count > 0) blocks.Add(currentBlock);

        var hasStructuredRoomReply = lines
            .TakeWhile(raw => !PromptLine().IsMatch(AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim()))
            .Any(string.IsNullOrWhiteSpace);
        // A complete room reply has one block with its title/exits/description, then a separate
        // object-and-people block. A leading or trailing blank line does not create a block.
        // Isolated incremental lines without this structure retain the specific-form fallback.
        var hasRoomObjectBlock = blocks.Count >= 2;
        IEnumerable<string> candidates = hasStructuredRoomReply
            ? hasRoomObjectBlock ? blocks[^1] : []
            : blocks.SelectMany(block => block);
        foreach (var plain in candidates)
        {
            var name = TryExtractGroundItemName(plain);
            // The MUD's room-object block is authoritative: an otherwise unfamiliar line is
            // still an object. Do not guess object types from its wording.
            if (name.Length == 0 && hasRoomObjectBlock) name = plain.TrimEnd('.').Trim();
            if (name.Length == 0) continue;
            // Room text decorates a character as "(NPK) Agron mezczyzna polork" while
            // GMCP supplies only "Agron". Remove the annotation before comparing the initial
            // character name, without treating an ordinary item as a person by guesswork.
            var comparableName = GetPlainItemName(name);
            var isPerson = people.Any(person =>
                string.Equals(comparableName, person, StringComparison.OrdinalIgnoreCase)
                || comparableName.StartsWith($"{person} ", StringComparison.OrdinalIgnoreCase));
            if (!isPerson) result.Add(new InventoryItem(name));
        }
        return result;
    }

    private static string TryExtractGroundItemName(string plain)
    {
        var match = GroundItemLine().Match(plain);
        var reverseMatch = GroundItemReverseLine().Match(plain);
        var seenMatch = GroundSeenItemLine().Match(plain);
        var attentionMatch = GroundAttentionItemLine().Match(plain);
        var floatingMatch = GroundFloatingItemLine().Match(plain);
        var madeMatch = GroundMadeItemLine().Match(plain);
        var locatedMatch = GroundLocatedItemLine().Match(plain);
        // Corpses legitimately say e.g. "lezy tu i psuje sie powoli". A standing
        // character with a continuation ("stoi tutaj i ...") is not a room object;
        // this remains safe even while the GMCP people list is still catching up.
        if (match.Success
            && string.Equals(match.Groups["verb"].Value, "stoi", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(match.Groups["tail"].Value))
        {
            match = Match.Empty;
        }
        // The observed chest presentation has no "lezy tu" suffix; retain only the known
        // container noun rather than treating arbitrary room-description sentences as items.
        var name = match.Success
            ? match.Groups["name"].Value.Trim()
            : reverseMatch.Success
                ? reverseMatch.Groups["name"].Value.Trim()
            : seenMatch.Success
                ? seenMatch.Groups["name"].Value.Trim()
            : attentionMatch.Success
                ? attentionMatch.Groups["name"].Value.Trim()
            : floatingMatch.Success
                ? floatingMatch.Groups["name"].Value.Trim()
            : madeMatch.Success
                ? madeMatch.Groups["name"].Value.Trim()
            : locatedMatch.Success && IsPotentialGroundContainer(locatedMatch.Groups["name"].Value)
                ? locatedMatch.Groups["name"].Value.Trim()
            : IsPotentialGroundContainer(plain) && plain.Contains(" wykonany z ", StringComparison.OrdinalIgnoreCase)
                ? plain.TrimEnd('.')
                : string.Empty;
        return name;
    }

    /// <summary>Returns candidate container names observed in room output. A candidate is never
    /// considered a container until its own <c>examine</c> result confirms a <c>zawiera:</c> block.</summary>
    public static bool IsPotentialGroundContainer(string itemName) =>
        Words(itemName).Any(PotentialGroundContainerWords.Contains);

    /// <summary>Recognizes the corpse nouns observed in ground-object lines. Corpses can expose
    /// contents, but are not closable or lockable containers.</summary>
    public static bool IsGroundCorpse(string itemName) =>
        Words(itemName).Any(word => word.Equals("cialo", StringComparison.OrdinalIgnoreCase)
            || word.Equals("trup", StringComparison.OrdinalIgnoreCase)
            || word.Equals("zwloki", StringComparison.OrdinalIgnoreCase)
            || word.Equals("szczatki", StringComparison.OrdinalIgnoreCase));

    /// <summary>Recognizes the observed server announcements that an opponent has died. This is
    /// intentionally narrower than generic death text, so it does not react to player-death UI.
    /// It permits either server form seen in combat logs: "nie zyje!!" or "pada na ziemie... MARTWY.".</summary>
    public static bool ContainsObservedOpponentDeath(string text) => text.Split('\n')
        .Select(Plain)
        .Any(line => line.EndsWith(" nie zyje!!", StringComparison.OrdinalIgnoreCase)
            || Regex.IsMatch(line, @"^.+? pada na ziemie\.\.\. MARTWY[!.]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));

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

    /// <summary>Returns name words suitable for retrying an item command when the server rejects
    /// an ambiguous word as a direction. Visual annotations and punctuation are excluded.</summary>
    public static IReadOnlyList<string> GetItemCommandWords(string itemName) => Words(itemName)
        .Where(word => word.Length >= 3)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>Recognizes the observed response produced when an item word is interpreted as a
    /// direction instead of an object name.</summary>
    public static bool IsDirectionQuestion(string response) =>
        AnsiText.StripKillerColors(AnsiText.StripAnsi(response))
            .Contains("W jakim kierunku chcesz spojrzec?", StringComparison.OrdinalIgnoreCase);

    /// <summary>Builds a reference for a caller-selected name word, retaining the observed
    /// ground → inventory → equipment occurrence order.</summary>
    public static ItemCommandReference ResolveItemCommandReferenceForWord(
        EquipmentInventorySnapshot snapshot,
        bool inInventory,
        int index,
        string word)
    {
        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var targetOffset = inInventory
            ? snapshot.GroundItems.Count + index
            : snapshot.GroundItems.Count + snapshot.Inventory.Count + index;
        var occurrence = ordered.Take(targetOffset + 1).Count(name => MatchesCommandWord(name, word));
        return new ItemCommandReference(word, occurrence);
    }

    public static ItemCommandReference ResolveGroundItemCommandReferenceForWord(
        EquipmentInventorySnapshot snapshot,
        int index,
        string word)
    {
        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        return new ItemCommandReference(word, ordered.Take(index + 1).Count(name => MatchesCommandWord(name, word)));
    }

    /// <summary>Checks whether an examine response still appears to concern the requested item.
    /// The MUD inflects Polish names in its responses, so words are compared by conservative
    /// stems instead of literal spellings. Every meaningful word from the requested name must
    /// have a matching response word; this deliberately does not decide whether to continue a
    /// command queue, only supplies a confidence signal to the caller.</summary>
    public static bool IsLikelyExamineResponseForItem(string response, string itemName)
    {
        var expected = GetPolishWordStems(itemName).Where(word => word.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (expected.Length == 0) return true;

        var actual = GetPolishWordStems(response).ToArray();
        return expected.All(expectedWord => actual.Any(actualWord =>
            string.Equals(expectedWord, actualWord, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Chooses the least ambiguous word from an item name against the server's combined
    /// room-ground-then-inventory-then-equipment lookup list. The MUD was observed to treat a command word as a
    /// prefix, therefore "krysztal" also matches "krysztalowy". One- and two-character connector
    /// words are ignored while a longer item-name word exists.</summary>
    public static ItemCommandReference ResolveItemCommandReference(EquipmentInventorySnapshot snapshot, string itemName, bool inInventory, int index)
    {
        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var targetOffset = inInventory ? snapshot.GroundItems.Count + index : snapshot.GroundItems.Count + snapshot.Inventory.Count + index;
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

    public static ItemCommandReference ResolveGroundItemCommandReference(EquipmentInventorySnapshot snapshot, string itemName, int index)
    {
        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var candidates = Words(itemName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var meaningfulCandidates = candidates.Where(word => word.Length >= 3).ToList();
        if (meaningfulCandidates.Count > 0) candidates = meaningfulCandidates;
        var selected = candidates.Select((word, position) => new { Word = word, Position = position, Matches = ordered.Count(name => MatchesCommandWord(name, word)) })
            .OrderBy(candidate => candidate.Matches).ThenBy(candidate => candidate.Position).FirstOrDefault();
        if (selected is null) return new ItemCommandReference(string.Empty, 1);
        return new ItemCommandReference(selected.Word, ordered.Take(index + 1).Count(name => MatchesCommandWord(name, selected.Word)));
    }

    /// <summary>Resolves a manually typed item argument to a carried-inventory index. The same
    /// observed command ordering applies as for game commands: ground items first, then inventory,
    /// then equipment. This lets a manually typed <c>3.torba</c> identify the container it names
    /// even when the client would have chosen a different, more unique word for that container.</summary>
    public static bool TryResolveInventoryItemIndex(EquipmentInventorySnapshot snapshot, string argument, out int inventoryIndex)
    {
        inventoryIndex = -1;
        var parts = argument.Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        var occurrence = parts.Length == 2 && int.TryParse(parts[0], out var parsedOccurrence)
            ? parsedOccurrence
            : 1;
        var word = parts.Length == 2 ? parts[1] : argument.Trim();
        if (occurrence < 1 || string.IsNullOrWhiteSpace(word)) return false;

        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name))
            .ToList();
        var matchedOffset = -1;
        var matchedCount = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (!MatchesCommandWord(ordered[index], word)) continue;
            if (++matchedCount != occurrence) continue;
            matchedOffset = index;
            break;
        }

        var inventoryStart = snapshot.GroundItems.Count;
        if (matchedOffset < inventoryStart || matchedOffset >= inventoryStart + snapshot.Inventory.Count) return false;
        inventoryIndex = matchedOffset - inventoryStart;
        return true;
    }

    /// <summary>Resolves a manually typed item argument to a ground-item index. It preserves
    /// the server lookup order (ground, then inventory, then equipment), including an optional
    /// <c>N.word</c> occurrence number.</summary>
    public static bool TryResolveGroundItemIndex(EquipmentInventorySnapshot snapshot, string argument, out int groundIndex)
    {
        groundIndex = -1;
        var parts = argument.Trim().Split('.', 2, StringSplitOptions.RemoveEmptyEntries);
        var occurrence = parts.Length == 2 && int.TryParse(parts[0], out var parsedOccurrence)
            ? parsedOccurrence
            : 1;
        var word = parts.Length == 2 ? parts[1] : argument.Trim();
        if (occurrence < 1 || string.IsNullOrWhiteSpace(word)) return false;

        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name))
            .ToList();
        var matchedCount = 0;
        for (var index = 0; index < ordered.Count; index++)
        {
            if (!MatchesCommandWord(ordered[index], word) || ++matchedCount != occurrence) continue;
            if (index >= snapshot.GroundItems.Count) return false;
            groundIndex = index;
            return true;
        }

        return false;
    }

    public static string BuildItemCommand(string verb, ItemCommandReference reference) =>
        string.IsNullOrWhiteSpace(reference.Word) ? verb : $"{verb} {reference.Argument}";

    /// <summary>Occurrence numbering follows the server order: room ground, inventory, then equipment.</summary>
    public static int GetExamineOccurrence(EquipmentInventorySnapshot snapshot, string itemName, bool inInventory, int index)
    {
        var word = FirstWord(itemName);
        var ordered = snapshot.GroundItems.Select(item => item.Name)
            .Concat(snapshot.Inventory.Select(item => item.Name))
            .Concat(snapshot.Equipment.Select(item => item.Name)).ToList();
        var targetOffset = inInventory ? snapshot.GroundItems.Count + index : snapshot.GroundItems.Count + snapshot.Inventory.Count + index;
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
    public static bool TryParseContainerContents(string response, string itemName, out IReadOnlyList<InventoryItem> contents, bool acceptServerContainerAlias = false)
    {
        var expectedName = GetPlainItemName(itemName);
        var lines = response.Split('\n');
        var headerIndex = Array.FindIndex(lines, line =>
        {
            var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line));
            plain = Regex.Replace(plain, @"\([^)]*\)", " ").Trim();
            const string suffix = " zawiera:";
            if (!plain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;
            var containerName = plain[..^suffix.Length].Trim();
            return string.Equals(containerName, expectedName, StringComparison.OrdinalIgnoreCase)
                || IsPotentialGroundContainer(expectedName) && IsPotentialGroundContainer(containerName)
                // The active command was sent only to a known potential/confirmed ground
                // container. Its response may abbreviate a corpse name (for example,
                // "Zmasakrowane cialo gwardzisty" -> "Cialo gwardzisty").
                || acceptServerContainerAlias;
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
            // Observed empty-container marker; it confirms the container but is not an item.
            if (string.Equals(plain, "Ogolnie nic.", StringComparison.OrdinalIgnoreCase)) continue;
            if (plain.Length > 0) result.Add(new InventoryItem(raw.Trim()));
        }
        contents = result;
        return true;
    }

    /// <summary>A standalone closed-state line in an examine response confirms that the examined
    /// object is a container, even though its contents cannot yet be listed.</summary>
    public static bool IsClosedContainerResponse(string response) => response.Split('\n')
        .Select(line => AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).Trim())
        .Any(line => string.Equals(line, "Zamkniete.", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line, "Zamkniety.", StringComparison.OrdinalIgnoreCase));

    public static bool IsContainerOpenedMessage(string line) => Plain(line)
        .StartsWith("Otwierasz ", StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerLockedMessage(string line) => Plain(line)
        .Contains("zamkniety na klucz", StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerUnlockedMessage(string line) => Plain(line)
        .StartsWith("Odkluczasz ", StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerLockedByKeyMessage(string line) => Plain(line)
        .StartsWith("Zamykasz ", StringComparison.OrdinalIgnoreCase)
        && Plain(line).Contains("na klucz", StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerClosedMessage(string line) => Plain(line)
        .StartsWith("Zamykasz ", StringComparison.OrdinalIgnoreCase)
        && !Plain(line).Contains("na klucz", StringComparison.OrdinalIgnoreCase);

    public static bool IsContainerKeyMissingMessage(string line)
    {
        var plain = Plain(line);
        return plain.Contains("brakuje ci", StringComparison.OrdinalIgnoreCase)
            && plain.Contains("klucz", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Extracts a server-confirmed container operation and its server-supplied name.
    /// This works independently of whether the player used a full command, abbreviation or alias.</summary>
    public static bool TryGetContainerOperationOutcome(string response, out ContainerOperationOutcome outcome, out string containerName)
    {
        foreach (var raw in response.Split('\n'))
        {
            var plain = Plain(raw);
            if (plain.StartsWith("Otwierasz ", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ContainerOperationOutcome.Opened;
                containerName = plain["Otwierasz ".Length..].Trim().TrimEnd('.').Trim();
                return containerName.Length > 0;
            }
            if (plain.StartsWith("Odkluczasz ", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ContainerOperationOutcome.Unlocked;
                containerName = plain["Odkluczasz ".Length..].Trim().TrimEnd('.').Trim();
                return containerName.Length > 0;
            }
            if (plain.StartsWith("Zamykasz ", StringComparison.OrdinalIgnoreCase)
                && plain.Contains(" na klucz", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ContainerOperationOutcome.Locked;
                containerName = plain["Zamykasz ".Length..plain.IndexOf(" na klucz", StringComparison.OrdinalIgnoreCase)].Trim();
                return containerName.Length > 0;
            }
            if (plain.StartsWith("Zamykasz ", StringComparison.OrdinalIgnoreCase))
            {
                outcome = ContainerOperationOutcome.Closed;
                containerName = plain["Zamykasz ".Length..].Trim().TrimEnd('.').Trim();
                return containerName.Length > 0;
            }
        }

        outcome = default;
        containerName = string.Empty;
        return false;
    }

    /// <summary>Matches a server-inflected container name to exactly one current ground object.
    /// Ambiguous names deliberately yield false rather than selecting an arbitrary container.</summary>
    public static bool TryResolveGroundContainerIndexFromServerName(EquipmentInventorySnapshot snapshot, string serverName, out int groundIndex)
    {
        groundIndex = -1;
        var serverWords = GetPolishWordStems(serverName).Where(word => word.Length >= 3).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (serverWords.Count == 0) return false;

        var candidates = snapshot.GroundItems.Select((item, index) => new
        {
            Index = index,
            Score = GetPolishWordStems(item.Name).Where(word => word.Length >= 3).Distinct(StringComparer.OrdinalIgnoreCase)
                .Count(serverWords.Contains)
        }).Where(candidate => candidate.Score > 0).ToArray();
        if (candidates.Length == 0) return false;

        var bestScore = candidates.Max(candidate => candidate.Score);
        var best = candidates.Where(candidate => candidate.Score == bestScore).ToArray();
        if (best.Length != 1) return false;
        groundIndex = best[0].Index;
        return true;
    }

    private static string Plain(string value) => AnsiText.StripKillerColors(AnsiText.StripAnsi(value)).Trim();

    /// <summary>Observed response to equipment commands while the character is asleep.</summary>
    public static bool IsSleepingCharacterResponse(string text) => Plain(text)
        .Contains("W snach czy co?", StringComparison.OrdinalIgnoreCase);

    /// <summary>Observed confirmation that a previously sleeping character can act again.</summary>
    public static bool IsWakeUpMessage(string text) => Plain(text)
        .Contains("Budzisz sie i wstajesz.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Recognizes only inventory mutations observed in server messages. A matching
    /// message means the top-level <c>inv</c> listing is stale, including when an item moved into
    /// or out of a carried container.</summary>
    public static bool IsInventoryMutationMessage(string line)
    {
        return GetInventoryMutationKind(line) is not null;
    }

    public static bool IsGroundDropMessage(string line) =>
        AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).TrimStart()
            .StartsWith("Upuszczasz ", StringComparison.OrdinalIgnoreCase);

    /// <summary>Observed server notices that an item has disintegrated. The notice does not say
    /// whether the item was carried or on the ground, so callers must reconcile both lists.</summary>
    public static bool IsItemDisintegrationMessage(string line)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).Trim();
        return Regex.IsMatch(plain, @"\b(rozsypuje sie w proch|rozpada(?:ja)? sie)\.$", RegexOptions.IgnoreCase);
    }

    /// <summary>Classifies only the successful inventory mutations observed in game logs.</summary>
    public static InventoryMutationKind? GetInventoryMutationKind(string line)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).TrimStart();
        // Observed server acknowledgement for taking currency: it changes the money counters,
        // but no item is added to the top-level inventory list.
        if (plain.StartsWith("Podnosisz kupke monet.", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Wyjmujesz kupke monet z ", StringComparison.OrdinalIgnoreCase)) return null;
        if (plain.StartsWith("Podnosisz ", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Kupujesz ", StringComparison.OrdinalIgnoreCase)
            || plain.Contains(" daje ci ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.Added;
        if (plain.StartsWith("Upuszczasz ", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Sprzedajesz ", StringComparison.OrdinalIgnoreCase)
            || plain.StartsWith("Dajesz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.Removed;
        if (plain.StartsWith("Wkladasz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.PutIntoContainer;
        if (plain.StartsWith("Wyjmujesz ", StringComparison.OrdinalIgnoreCase)) return InventoryMutationKind.TakenFromContainer;
        return null;
    }

    /// <summary>Extracts the observed successful ground-pickup target. It is intentionally
    /// limited to the server's "Podnosisz &lt;item&gt;." acknowledgement.</summary>
    public static bool TryGetPickedUpItemName(string line, out string itemName)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(line)).Trim();
        const string prefix = "Podnosisz ";
        if (!plain.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            itemName = string.Empty;
            return false;
        }

        itemName = plain[prefix.Length..].Trim().TrimEnd('.').Trim();
        return itemName.Length > 0;
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
        // Commands accept words, not display punctuation such as quotation marks or commas.
        plain = ItemNamePunctuation().Replace(plain, string.Empty);
        return plain.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static IEnumerable<string> GetPolishWordStems(string text)
    {
        var plain = AnsiText.StripKillerColors(AnsiText.StripAnsi(text));
        plain = Regex.Replace(plain, @"\([^)]*\)", " ");
        foreach (Match match in LetterWords().Matches(plain))
        {
            yield return PolishStem(match.Value);
        }
    }

    private static string PolishStem(string word)
    {
        var value = word.ToLowerInvariant();
        // Longest endings first. The guard keeps short words such as "oko" or "pas" intact.
        foreach (var ending in new[] { "owymi", "owego", "owych", "owej", "owemu", "ami", "ach", "owie", "owa", "owe", "owy", "ego", "emu", "iej", "owi", "ami", "ach", "ie", "om", "ów", "ą", "ę", "a", "e", "y", "i", "u" })
        {
            if (value.EndsWith(ending, StringComparison.Ordinal) && value.Length - ending.Length >= 4)
            {
                return value[..^ending.Length];
            }
        }

        return value;
    }

    private static bool MatchesCommandWord(string itemName, string commandWord) =>
        Words(itemName).Any(candidate => candidate.StartsWith(commandWord, StringComparison.OrdinalIgnoreCase));

    private static string NormalizeItemName(string? text) =>
        string.Join(' ', Words(text ?? string.Empty));
}
