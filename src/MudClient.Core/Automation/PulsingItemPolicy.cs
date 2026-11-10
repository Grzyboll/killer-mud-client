namespace MudClient.Core.Automation;

/// <summary>
/// Finds items tagged "(pulsuje)" in a corpse/room examine response — this MUD's generic marker
/// for a magically-charged item, prepended to the item's own name regardless of what that name
/// actually is (unlike e.g. Killeropedia.RandomBookNaming's fixed word pools for random books).
/// "pulsuje" itself is never a valid "get" keyword — it's a decorative status tag, not part of the
/// item's own name — so the first real word after it is used instead, confirmed by hand-testing
/// that a pulsing item's own name words (e.g. "helm" or "smoka" in "(pulsuje) helm z czaszki
/// smoka") work as "get" keywords while "get pulsuje" does not.
/// </summary>
public static class PulsingItemPolicy
{
    private const string PulsingTag = "(pulsuje)";

    /// <summary>Every "get"-able keyword for a pulsing item named in <paramref name="line"/> — one
    /// per "(pulsuje)" occurrence, taken from the first word of that item's own name. Empty when
    /// the line doesn't mention one. E.g. "(pulsuje) helm z czaszki smoka" -&gt; ["helm"].</summary>
    public static IReadOnlyList<string> FindPulsingItemKeywords(string line)
    {
        List<string>? keywords = null;
        var searchFrom = 0;
        while (true)
        {
            var tagIndex = line.IndexOf(PulsingTag, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (tagIndex < 0)
            {
                break;
            }

            searchFrom = tagIndex + PulsingTag.Length;
            var afterTag = line[searchFrom..].TrimStart();
            var firstWord = afterTag
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()
                ?.Trim(',', '.', ';', ':');
            if (string.IsNullOrWhiteSpace(firstWord))
            {
                continue;
            }

            keywords ??= [];
            keywords.Add(firstWord);
        }

        return keywords ?? [];
    }
}
