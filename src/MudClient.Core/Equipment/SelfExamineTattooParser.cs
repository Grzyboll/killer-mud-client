using MudClient.Core.Text;

namespace MudClient.Core.Equipment;

public sealed record TattooItem(string Location, string Name, string Description);

/// <summary>Extracts only tattoo blocks observed in the player's <c>examine self</c> response.
/// All character prose and the following equipment table are deliberately ignored.</summary>
public static class SelfExamineTattooParser
{
    public static IReadOnlyList<TattooItem> Parse(string response)
    {
        var result = new List<TattooItem>();
        string? location = null;
        string? name = null;
        var bonuses = new List<string>();

        void CompleteCurrent()
        {
            if (location is not null && name is not null)
            {
                result.Add(new TattooItem(location, name, string.Join("\n", bonuses)));
            }
            location = null;
            name = null;
            bonuses.Clear();
        }

        foreach (var raw in response.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = AnsiText.StripKillerColors(AnsiText.StripAnsi(raw)).Trim();
            if (line.StartsWith("Na ", StringComparison.OrdinalIgnoreCase)
                && line.Contains(" masz ", StringComparison.OrdinalIgnoreCase)
                && line.Contains("tatuaz", StringComparison.OrdinalIgnoreCase))
            {
                CompleteCurrent();
                var separator = line.IndexOf(" masz ", StringComparison.OrdinalIgnoreCase);
                location = line[3..separator].Trim();
                name = line[(separator + " masz ".Length)..].Trim().TrimEnd('.');
                continue;
            }

            if (name is not null && line.Length >= 3 && line[0] == '[' && line[^1] == ']')
            {
                bonuses.Add(line);
                continue;
            }

            if (name is not null && line.Length > 0)
            {
                CompleteCurrent();
            }
        }

        CompleteCurrent();
        return result;
    }
}
