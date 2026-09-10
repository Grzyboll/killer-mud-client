using MudClient.Core.Text;

namespace MudClient.App.Services;

/// <summary>
/// Keeps annotations added to the MUD's multi-column command output from pushing the next
/// original cell to the right. Annotated cells are re-emitted one per line instead.
/// </summary>
internal static class AnnotatedCommandColumnFormatter
{
    public static string Format(
        string prefix,
        IReadOnlyList<string> cells,
        bool headerOnOwnLine)
    {
        if (cells.Count == 0)
        {
            return prefix;
        }

        var output = new List<string>();
        var header = prefix.TrimEnd();
        if (headerOnOwnLine && header.Length > 0)
        {
            // A level/circle label belongs to the row group, not to its first cell. Keeping it
            // separate gives every cell in that group the exact same left edge.
            output.Add(header);
        }

        var firstCellPrefix = headerOnOwnLine ? "  " : prefix;
        var continuationPrefix = headerOnOwnLine
            ? "  "
            : new string(' ', VisibleLength(prefix));
        for (var index = 0; index < cells.Count; index++)
        {
            var linePrefix = index == 0 ? firstCellPrefix : continuationPrefix;
            output.Add(linePrefix + cells[index]);
        }
        return string.Join('\n', output);
    }

    private static int VisibleLength(string value) => AnsiText.StripAnsi(value).Length;

}
