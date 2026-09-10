using System.Text;
using System.Text.RegularExpressions;
using MudClient.App.Models;
using MudClient.Core.Text;

namespace MudClient.App.Services;

internal static partial class SkillListColumnFormatter
{
    private const string SkillColor = "\u001b[36m";
    private const string LevelColor = "\u001b[37m";
    private const string ResetColor = "\u001b[0m";
    [GeneratedRegex(@"\[WW\]\s+(?<name>\S(?:.*?\S)?)\s{2,}(?<learnable>\d+)\s+(?<current>\d+)\s*\+\s*(?<bonus>\d+)")]
    private static partial Regex Row();

    public static string Format(string text, IReadOnlyList<TeacherEntry> teachers)
    {
        var groups = new List<(string Header, List<string> Cells)>();
        var footer = new List<string>();
        List<string>? cells = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var (plain, map) = AnsiText.StripAnsiWithMap(line);
            var matches = Row().Matches(plain);
            if (matches.Count == 0) { if (plain.Contains("Ograniczenia skilli", StringComparison.OrdinalIgnoreCase)) footer.Add(line); continue; }
            if (plain.Contains("Poziom", StringComparison.OrdinalIgnoreCase))
            {
                cells = new List<string>();
                groups.Add((plain[..matches[0].Index].TrimEnd(), cells));
            }
            if (cells is null) continue;
            for (var matchIndex = 0; matchIndex < matches.Count; matchIndex++)
            {
                var match = matches[matchIndex];
                var start = map[match.Index]; var endPlain = match.Index + match.Length;
                var end = endPlain <= map.Count ? map[endPlain - 1] + 1 : line.Length;
                var cell = line[start..end];
                if (matchIndex == 0)
                    cell = SkillColor + cell;
                var trainer = SkillTrainerAnnotator.FindBestTrainer(match.Groups["name"].Value.Trim(), int.Parse(match.Groups["current"].Value), teachers);
                if (trainer is not null) cell += " (" + trainer + ')';
                cells.Add(cell);
            }
        }
        if (groups.Count == 0) return text;
        var widths = new int[2];
        foreach (var g in groups) for (var i=0;i<g.Cells.Count;i++) widths[i%2]=Math.Max(widths[i%2],AnsiText.StripAnsi(g.Cells[i]).Length);
        var output=new StringBuilder();
        foreach(var g in groups){ output.Append(LevelColor).Append(g.Header).Append(ResetColor).Append('\n'); for(var i=0;i<g.Cells.Count;i+=2){var a=g.Cells[i];output.Append(a);if(i+1<g.Cells.Count)output.Append(' ',widths[0]-AnsiText.StripAnsi(a).Length+2).Append(g.Cells[i+1]);output.Append('\n');}}
        foreach (var line in footer) output.Append(line).Append('\n');
        return output.ToString();
    }
}
