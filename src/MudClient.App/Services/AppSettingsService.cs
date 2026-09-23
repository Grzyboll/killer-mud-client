using System.IO;
using System.Text.Json;
using MudClient.App.Models;
using MudClient.App.Controls;

namespace MudClient.App.Services;

/// <summary>
/// Stores application-wide settings as a single JSON file.
/// Default location: %AppData%\KillerMudClient\settings.json.
/// </summary>
public sealed class AppSettingsService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _path;

    public AppSettingsService(string? directory = null)
    {
        var folder = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "KillerMudClient");
        _path = Path.Combine(folder, "settings.json");
        DirectoryPath = folder;
    }

    public string DirectoryPath { get; }

    public AppSettings Load()
    {
        if (DurableJsonFile.TryRead<AppSettings>(_path, SerializerOptions, out var settings)
            && settings is not null)
        {
            settings.OutputFontSize = Math.Clamp(
                settings.OutputFontSize, AppSettings.MinOutputFontSize, AppSettings.MaxOutputFontSize);
            if (string.IsNullOrWhiteSpace(settings.OutputFontFamily))
            {
                settings.OutputFontFamily = AppSettings.DefaultOutputFontFamily;
            }

            settings.WidgetFontSize = Math.Clamp(
                settings.WidgetFontSize, AppSettings.MinWidgetFontSize, AppSettings.MaxWidgetFontSize);
            if (string.IsNullOrWhiteSpace(settings.WidgetFontFamily))
            {
                settings.WidgetFontFamily = AppSettings.DefaultWidgetFontFamily;
            }

            settings.TerminalMaxLines = Math.Clamp(
                settings.TerminalMaxLines, AppSettings.MinTerminalMaxLines, AppSettings.MaxTerminalMaxLines);

            if (!AnsiColorPalette.IsKnown(settings.TelnetColorScheme))
            {
                settings.TelnetColorScheme = AppSettings.DefaultTelnetColorScheme;
            }

            // null means the property is missing from an older/corrupt settings file — use default.
            if (settings.CommandStackingSeparator is null)
            {
                settings.CommandStackingSeparator = AppSettings.DefaultCommandStackingSeparator;
            }
            else
            {
                // Trim whitespace to be consistent with the UI setter in MainWindowViewModel,
                // but preserve an explicitly-saved empty string (disables command stacking).
                settings.CommandStackingSeparator = settings.CommandStackingSeparator.Trim();
            }

            settings.TerminalOverlays = (settings.TerminalOverlays ?? [])
                .Where(overlay => !string.IsNullOrWhiteSpace(overlay.PanelId))
                .GroupBy(overlay => overlay.PanelId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
            foreach (var overlay in settings.TerminalOverlays)
            {
                overlay.HeightWeight = Math.Clamp(
                    overlay.HeightWeight,
                    AppSettings.MinTerminalOverlayHeightWeight,
                    AppSettings.MaxTerminalOverlayHeightWeight);
                overlay.ColumnWidth = Math.Clamp(
                    overlay.ColumnWidth,
                    AppSettings.MinTerminalOverlayColumnWidth,
                    AppSettings.MaxTerminalOverlayColumnWidth);
                overlay.ColumnHeightFraction = Math.Clamp(
                    overlay.ColumnHeightFraction,
                    AppSettings.MinTerminalOverlayColumnHeightFraction,
                    AppSettings.MaxTerminalOverlayColumnHeightFraction);
                overlay.ColumnIndex = Math.Max(0, overlay.ColumnIndex);
            }

            settings.TerminalOverlayOpacity = Math.Clamp(
                settings.TerminalOverlayOpacity,
                AppSettings.MinTerminalOverlayOpacity,
                AppSettings.MaxTerminalOverlayOpacity);

            settings.AutowalkLowMovementThresholdPercent = Math.Clamp(
                settings.AutowalkLowMovementThresholdPercent,
                AppSettings.MinAutowalkLowMovementThresholdPercent,
                AppSettings.MaxAutowalkLowMovementThresholdPercent);

            settings.AutowalkRestSeconds = Math.Clamp(
                settings.AutowalkRestSeconds,
                AppSettings.MinAutowalkRestSeconds,
                AppSettings.MaxAutowalkRestSeconds);

            return settings;
        }

        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        DurableJsonFile.Write(_path, settings, SerializerOptions);
    }
}
