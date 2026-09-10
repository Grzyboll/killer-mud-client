using MudClient.Core.Gmcp;

namespace MudClient.App.Models;

/// <summary>
/// Memorized spells of a single magic circle, populated from Char.MemSpell GMCP.
/// Spells are aggregated by name ("cure serious ×7"); slots still being
/// memorized (meming) are shown separately from finished (memed) ones.
/// </summary>
public sealed record MemSpellCircle
{
    public int Circle { get; }
    public int MemedCount { get; }
    public int MemingCount { get; }
    public int UnmemedCount { get; }
    public string MemedDisplay { get; }
    public string MemingDisplay { get; }
    public string UnmemedDisplay { get; }

    public string Header => $"Krąg {Circle}";

    /// <summary>Ready versus used slots, e.g. "7/2".</summary>
    public string CountDisplay => $"{MemedCount}/{UnmemedCount}";

    public bool HasMemed => MemedDisplay.Length > 0;
    public bool HasMeming => MemingDisplay.Length > 0;
    public bool HasUnmemed => UnmemedDisplay.Length > 0;

    private MemSpellCircle(
        int circle,
        int memedCount,
        int memingCount,
        int unmemedCount,
        string memedDisplay,
        string memingDisplay,
        string unmemedDisplay)
    {
        Circle = circle;
        MemedCount = memedCount;
        MemingCount = memingCount;
        UnmemedCount = unmemedCount;
        MemedDisplay = memedDisplay;
        MemingDisplay = memingDisplay;
        UnmemedDisplay = unmemedDisplay;
    }

    /// <summary>Groups a flat slot list into per-circle rows, ordered by circle.</summary>
    public static List<MemSpellCircle> FromCore(IReadOnlyList<MemorizedSpell> spells)
    {
        var result = new List<MemSpellCircle>();
        foreach (var circleGroup in spells.GroupBy(s => s.Circle).OrderBy(g => g.Key))
        {
            var memed = circleGroup.Where(s => s.Memed && !s.Meming).ToList();
            var meming = circleGroup.Where(s => s.Meming).ToList();
            var unmemed = circleGroup.Where(s => !s.Memed && !s.Meming).ToList();
            result.Add(new MemSpellCircle(
                circle: circleGroup.Key,
                memedCount: memed.Count,
                memingCount: meming.Count,
                unmemedCount: unmemed.Count,
                memedDisplay: Aggregate(memed),
                memingDisplay: Aggregate(meming),
                unmemedDisplay: Aggregate(unmemed)));
        }

        return result;
    }

    /// <summary>"armor, cure serious ×7" — names in slot (counter) order, duplicates collapsed.</summary>
    private static string Aggregate(IEnumerable<MemorizedSpell> spells)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var spell in spells.OrderBy(s => s.Counter))
        {
            if (counts.TryGetValue(spell.Name, out var count))
            {
                counts[spell.Name] = count + 1;
            }
            else
            {
                counts[spell.Name] = 1;
                order.Add(spell.Name);
            }
        }

        return string.Join(", ", order.Select(name =>
            counts[name] > 1 ? $"{name} ×{counts[name]}" : name));
    }
}
