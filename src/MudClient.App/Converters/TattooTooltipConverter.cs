using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using MudClient.App.Controls;
using MudClient.App.Models;

namespace MudClient.App.Converters;

/// <summary>Builds the same bordered, opaque-dark tooltip card <see cref="AbilityTooltipConverter"/>
/// builds for an ability row (see <see cref="AbilitySkillTreeCanvas.BuildTattooTooltip"/>) for a
/// tattoo bonus row instead — used by the "Sprawdź co zyskasz" tattoo list in
/// KilleropediaSkillsView, so it doesn't fall back to the platform's default (here, illegible)
/// tooltip chrome for a bare string.</summary>
public sealed class TattooTooltipConverter : IValueConverter
{
    public static readonly TattooTooltipConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TattooBonusEntry tattoo ? AbilitySkillTreeCanvas.BuildTattooTooltip(tattoo) : AvaloniaProperty.UnsetValue;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
