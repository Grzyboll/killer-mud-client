using System.Text.RegularExpressions;
using MudClient.App.Models;
using MudClient.Core.Text;

namespace MudClient.App.Services;

/// <summary>
/// Splices " (Nauczyciel)" onto each row of the "skill" command's output (e.g.
/// "[WW]  axe                 10   3 + 0" -&gt; "...+ 0 (Mistrz Moran)"), naming the single most
/// useful currently known Killeropedia teacher who can still train the player further in that
/// skill. See <see cref="FindBestTrainer"/> for the eligibility/ranking rule — a common starter
/// skill can have a dozen eligible teachers, which would make the line unreadable if all were
/// listed, so only the one that can take the player furthest is shown.
/// </summary>
public static class SkillTrainerAnnotator
{
    private const string SkillLabelColor = "\u001b[36m";
    private sealed record ParsedRow(string Prefix, IReadOnlyList<string> Cells);
    // "[WW]  <name, one or more words>  <learnable>  <current> + <bonus>" — two such entries
    // typically share one line. The name is separated from its numbers by 2+ spaces (the table's
    // own column padding), which is what lets a multi-word name like "twohanded weapon" or "wiez
    // z magia odrzucen" be captured without also swallowing the numbers that follow it.
    private static readonly Regex SkillRowPattern = new(
        @"\[WW\]\s+(?<name>\S(?:.*?\S)?)\s{2,}(?<learnable>\d+)\s+(?<current>\d+)\s*\+\s*(?<bonus>\d+)",
        RegexOptions.Compiled);

    /// <summary>Returns <paramref name="line"/> unchanged unless it contains at least one
    /// recognized skill row. Matches against an ANSI-stripped copy of <paramref name="line"/> —
    /// this MUD colors the skill/current/bonus numbers in its "skill" output, and those escape
    /// codes sit right inside what would otherwise be plain whitespace between tokens, which
    /// silently broke every match before this used <see cref="AnsiText.StripAnsiWithMap"/> to see
    /// through them. The annotation itself is still spliced into the original, colored
    /// <paramref name="line"/> — never into the stripped copy — so existing coloring survives.</summary>
    public static string Annotate(string line, IReadOnlyList<TeacherEntry> teachers)
    {
        return TryParseRow(line, teachers, out var row)
            ? AnnotatedCommandColumnFormatter.Format(row.Prefix, row.Cells, headerOnOwnLine: true)
            : line;
    }

    private static bool TryParseRow(string line, IReadOnlyList<TeacherEntry> teachers, out ParsedRow row)
    {
        row = null!;
        if (teachers.Count == 0)
        {
            return false;
        }

        var (plain, originalIndexes) = AnsiText.StripAnsiWithMap(line);
        if (!plain.Contains("[WW]", StringComparison.Ordinal))
        {
            return false;
        }

        var matches = SkillRowPattern.Matches(plain);
        if (matches.Count == 0)
        {
            return false;
        }

        var annotatedCells = new List<string>(matches.Count);
        var firstMatchStartInLine = originalIndexes[matches[0].Index];
        foreach (Match match in matches)
        {
            var matchStartInLine = originalIndexes[match.Index];
            var matchEndInPlain = match.Index + match.Length;
            var matchEndInLine = matchEndInPlain <= originalIndexes.Count
                ? originalIndexes[matchEndInPlain - 1] + 1
                : line.Length;

            var cell = line[matchStartInLine..matchEndInLine];

            var skillName = match.Groups["name"].Value.Trim();
            var current = int.Parse(match.Groups["current"].Value);
            if (FindBestTrainer(skillName, current, teachers) is { } trainer)
            {
                cell += " (" + trainer + ')';
            }

            annotatedCells.Add(cell);
        }

        // The ANSI sequence that colors the first [WW] label occurs immediately before its
        // visible text. Moving the level header onto its own line would otherwise leave that
        // first cell white, while all later cells retain their own color sequence.
        annotatedCells[0] = SkillLabelColor + annotatedCells[0];

        row = new ParsedRow(line[..firstMatchStartInLine], annotatedCells);
        return true;
    }

    /// <summary>The single most useful teacher who can still train <paramref name="skillName"/>
    /// beyond <paramref name="currentValue"/>: among every teacher the player already meets the
    /// "wymaga" threshold for (<see cref="TeacherSkillEntry.RequiredSkill"/>) and hasn't already
    /// outgrown (<see cref="TeacherSkillEntry.Max"/> — unbounded when null), picks whichever can
    /// take them furthest (highest Max), so the player doesn't have to immediately switch
    /// teachers again after training. Ties (including two unbounded teachers) break on name.
    /// Returns null when no teacher currently qualifies.</summary>
    internal static string? FindBestTrainer(
        string skillName, int currentValue, IReadOnlyList<TeacherEntry> teachers)
    {
        string? bestName = null;
        var bestMax = int.MinValue;

        foreach (var teacher in teachers)
        {
            foreach (var skill in teacher.Skills)
            {
                if (!string.Equals(skill.Name, skillName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (currentValue < skill.RequiredSkill)
                {
                    continue;
                }

                var effectiveMax = skill.Max ?? int.MaxValue;
                if (currentValue >= effectiveMax)
                {
                    continue;
                }

                var isBetter = effectiveMax > bestMax
                    || (effectiveMax == bestMax && bestName is not null
                        && string.Compare(teacher.Name, bestName, StringComparison.OrdinalIgnoreCase) < 0);
                if (isBetter)
                {
                    bestMax = effectiveMax;
                    bestName = teacher.Name;
                }
            }
        }

        return bestName;
    }
}
