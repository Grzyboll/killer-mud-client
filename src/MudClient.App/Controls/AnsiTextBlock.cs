using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System.Text;

namespace MudClient.App.Controls;

/// <summary>Compact, selectable panel-row renderer which preserves the game's ANSI foreground,
/// background, bold and underline styles without requiring a terminal scrollback.</summary>
public sealed class AnsiTextBlock : TextBlock
{
    public static readonly StyledProperty<string> AnsiTextProperty =
        AvaloniaProperty.Register<AnsiTextBlock, string>(nameof(AnsiText), string.Empty);
    public string AnsiText { get => GetValue(AnsiTextProperty); set => SetValue(AnsiTextProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == AnsiTextProperty) Render(AnsiText);
    }

    private void Render(string text)
    {
        Inlines?.Clear();
        var parser = new AnsiStreamParser();
        // The item text was separated from the MUD's slot prefix. Start a new ANSI state even
        // when that prefix had opened a style which would otherwise leak into this standalone row.
        foreach (var token in parser.Feed("\u001b[0m" + ConvertKillerColors(text)))
        {
            if (token is not AnsiTextToken part) continue;
            var run = new Run(part.Text)
            {
                // Run does not reliably inherit TextBlock.Foreground in Avalonia's inline
                // renderer; assign the terminal's visible default explicitly for reset/white text.
                Foreground = part.Style.Foreground is { } foreground ? new SolidColorBrush(foreground) : Brushes.White,
                Background = part.Style.Background is { } background ? new SolidColorBrush(background) : null,
                FontWeight = part.Style.Bold ? FontWeight.Bold : FontWeight.Normal,
                TextDecorations = part.Style.Underline ? Avalonia.Media.TextDecorations.Underline : null,
            };
            Inlines?.Add(run);
        }
    }

    private static string ConvertKillerColors(string text)
    {
        var output = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '{' && index + 1 < text.Length && ColorCodes.TryGetValue(text[index + 1], out var code))
            { output.Append("\u001b[").Append(code).Append('m'); index++; }
            else output.Append(text[index]);
        }
        return output.ToString();
    }

    private static readonly IReadOnlyDictionary<char, int> ColorCodes = new Dictionary<char, int>
    {
        ['x'] = 0, ['r'] = 31, ['R'] = 91, ['g'] = 32, ['G'] = 92, ['y'] = 33, ['Y'] = 93,
        ['b'] = 34, ['B'] = 94, ['m'] = 35, ['M'] = 95, ['c'] = 36, ['C'] = 96, ['w'] = 37, ['W'] = 97,
    };
}
