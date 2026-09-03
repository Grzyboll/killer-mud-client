using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace MudClient.App.Converters;

/// <summary>Reddens a group spell-shortcut button (see GroupPanelView.axaml) when the caster
/// doesn't currently have that spell memorized and ready — a quick warning that clicking it will
/// fail rather than cast. Bound as [SpellName, MainWindowViewModel.MemorizedSpellNames]; returns
/// <see cref="AvaloniaProperty.UnsetValue"/> when memorized (or on missing/malformed input) so the
/// button's own style (hover, pressed, etc.) keeps controlling its background instead of being
/// pinned to a fixed "normal" brush.</summary>
public sealed class SpellMemorizedBrushConverter : IMultiValueConverter
{
    public static readonly SpellMemorizedBrushConverter Instance = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2 ||
            values[0] is not string spellName ||
            values[1] is not IEnumerable<string> memorizedNames)
        {
            return AvaloniaProperty.UnsetValue;
        }

        if (memorizedNames.Contains(spellName, StringComparer.OrdinalIgnoreCase))
        {
            return AvaloniaProperty.UnsetValue;
        }

        return Application.Current?.TryFindResource("MudBrushCrimson", out var brush) == true
            ? brush
            : Brushes.Crimson;
    }
}

/// <summary>Colors the small "mem" indicator next to a tracked buff's name in the Mem i Buffy
/// panel — the theme's default (light/parchment) foreground when
/// <see cref="Models.BuffWatchEntry.IsMemorized"/> is true, crimson red when it's false. Unlike
/// <see cref="SpellMemorizedBrushConverter"/> (which only reddens a button background, leaving the
/// "memorized" case unstyled), this always returns a concrete brush so the label reads correctly
/// standing alone rather than relying on a caller's own default foreground.</summary>
public sealed class MemorizedIndicatorForegroundConverter : IValueConverter
{
    public static readonly MemorizedIndicatorForegroundConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            return Application.Current?.TryFindResource("MudBrushParchment", out var brush) == true
                ? brush
                : Brushes.White;
        }

        return Application.Current?.TryFindResource("MudBrushCrimsonBright", out var crimsonBrush) == true
            ? crimsonBrush
            : Brushes.Crimson;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
