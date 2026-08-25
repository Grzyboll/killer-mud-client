using System.Text.RegularExpressions;
using MudClient.Core.Text;

namespace MudClient.Core.Automation;

/// <summary>
/// Client-side workaround for a MUD that restricts the "order" command (see
/// <see cref="GroupOrderPolicy"/>) to the formal group leader — the server itself rejects an
/// "order" attempt from anyone else with "Nie jesteś przywódcą tej grupy.", so no client trick can
/// make that command work without real in-game leadership. Instead, a trusted character can relay
/// a command over ordinary "say" — a channel the MUD lets any player use — and this recognizes it.
/// Only a "!"-prefixed say (e.g. "!stand") is treated as a command; everything else that character
/// says stays plain chat, so casual conversation on the group's own leader account never gets
/// executed by accident.
/// </summary>
public static partial class RemoteCommandPolicy
{
    public static bool TryGetCommand(string line, string? trustedCharacterName, out string command)
    {
        command = string.Empty;
        if (string.IsNullOrWhiteSpace(trustedCharacterName))
        {
            return false;
        }

        var match = SayRegex().Match(AnsiText.StripAnsi(line));
        if (!match.Success)
        {
            return false;
        }

        var speaker = match.Groups["speaker"].Value;
        if (!string.Equals(speaker, trustedCharacterName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        command = match.Groups["command"].Value.Trim();
        return command.Length > 0;
    }

    // Same "X mówi ... 'text'" shape ChatLinePolicy already recognizes for a plain say, scoped to
    // just the one verb and the "!"-prefixed capture this feature needs.
    [GeneratedRegex(@"^(?<speaker>\w+) m[oó]wi.*'!(?<command>.+)'\.?$", RegexOptions.CultureInvariant)]
    private static partial Regex SayRegex();
}
