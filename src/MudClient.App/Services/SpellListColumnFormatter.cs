using System.Text;
using System.Text.RegularExpressions;
using MudClient.App.Models;
using MudClient.Core.Text;

namespace MudClient.App.Services;

internal static partial class SpellListColumnFormatter
{
    [GeneratedRegex(@"Kr[ąa]g\s+\d+:", RegexOptions.IgnoreCase)]
    private static partial Regex CircleHeader();

    [GeneratedRegex(@"\((?<count>[^)]*)\)(?:\[\d+\])?\s+(?<name>\S(?:.*?\S)?)(?=\s{2,}|\s*$)")]
    private static partial Regex SpellRow();

    public static string Format(string text, IReadOnlyList<SpellMobEntry> mobs)
    {
        var rows = new List<(string Header, List<string> Cells)>();
        var footer = new List<string>();
        List<string>? cells = null;
        string header = string.Empty;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var annotated = SpellSourceAnnotator.Annotate(line, mobs);
            var (plain, map) = AnsiText.StripAnsiWithMap(annotated);
            var matches = SpellRow().Matches(plain);
            if (matches.Count == 0)
            {
                if (plain.Contains("Liczba znanych", StringComparison.OrdinalIgnoreCase)
                    || plain.Contains("Aby sprawdzi", StringComparison.OrdinalIgnoreCase))
                    footer.Add(line);
                continue;
            }

            if (CircleHeader().IsMatch(plain))
            {
                cells = new List<string>();
                var headerEnd = FindCellStart(annotated, map[matches[0].Index]);
                rows.Add((annotated[..headerEnd].TrimEnd(), cells));
            }

            if (cells is null)
            {
                continue;
            }

            foreach (Match match in matches)
            {
                var start = FindCellStart(annotated, map[match.Index]);
                var endPlain = match.Index + match.Length;
                var end = endPlain <= map.Count ? map[endPlain - 1] + 1 : annotated.Length;
                cells.Add(annotated[start..end]);
            }
        }

        if (rows.Count == 0)
        {
            return text;
        }

        var widths = new int[3];
        foreach (var row in rows)
            for (var i = 0; i < row.Cells.Count; i++)
                widths[i % 3] = Math.Max(widths[i % 3], AnsiText.StripAnsi(row.Cells[i]).Length);

        var output = new StringBuilder();
        foreach (var row in rows)
        {
            output.Append(row.Header).Append('\n');
            for (var i = 0; i < row.Cells.Count; i += 3)
            {
                for (var column = 0; column < 3 && i + column < row.Cells.Count; column++)
                {
                    var cell = row.Cells[i + column];
                    output.Append(cell);
                    if (column < 2 && i + column + 1 < row.Cells.Count)
                        output.Append(' ', widths[column] - AnsiText.StripAnsi(cell).Length + 2);
                }
                output.Append('\n');
            }
        }
        foreach (var line in footer)
            output.Append(line).Append('\n');
        output.Append("\r\n");
        return output.ToString();
    }

    private static int FindCellStart(string text, int visibleStart)
    {
        var escape = text.LastIndexOf('\u001b', visibleStart - 1);
        if (escape < 0)
        {
            return visibleStart;
        }

        var terminator = text.IndexOf('m', escape);
        return terminator == visibleStart - 1 ? escape : visibleStart;
    }
}
