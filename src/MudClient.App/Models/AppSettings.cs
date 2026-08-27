using MudClient.App.Controls;

namespace MudClient.App.Models;

/// <summary>
/// Application-wide (not per-profile) settings, stored in %AppData%\KillerMudClient\settings.json.
/// </summary>
public sealed class AppSettings
{
    public const string DefaultOutputFontFamily = "Consolas";
    public const double DefaultOutputFontSize = 14;
    public const double MinOutputFontSize = 9;
    public const double MaxOutputFontSize = 28;
    public const string DefaultWidgetFontFamily = "Inter";
    public const double DefaultWidgetFontSize = 13;
    public const double MinWidgetFontSize = 9;
    public const double MaxWidgetFontSize = 24;
    public const string DefaultTelnetColorScheme = "Ciepłe";

    /// <summary>Default/limits for the terminal overlay's shared transparency (see
    /// <see cref="TerminalOverlayOpacity"/>).</summary>
    public const double DefaultTerminalOverlayOpacity = 0.85;
    public const double MinTerminalOverlayOpacity = 0.2;
    public const double MaxTerminalOverlayOpacity = 1.0;

    /// <summary>Default/limits for one overlay column's width in pixels (see
    /// <see cref="TerminalOverlayEntry.ColumnWidth"/>). Columns float on top of the Terminal and
    /// never resize it, so this is a plain pixel size rather than a fraction of anything.</summary>
    public const double DefaultTerminalOverlayColumnWidth = 320;
    public const double MinTerminalOverlayColumnWidth = 200;
    public const double MaxTerminalOverlayColumnWidth = 900;

    /// <summary>Default/limits for one overlay column's overall height, as a fraction (0..1) of
    /// the Terminal's own height (see <see cref="TerminalOverlayEntry.ColumnHeightFraction"/>).
    /// The stack is anchored to the top, so shrinking this reveals terminal below the last
    /// card.</summary>
    public const double DefaultTerminalOverlayColumnHeightFraction = 1.0;
    public const double MinTerminalOverlayColumnHeightFraction = 0.2;
    public const double MaxTerminalOverlayColumnHeightFraction = 1.0;

    /// <summary>Default/limits for one overlay's height relative to the others stacked in the
    /// same column (a Grid star weight — see <see cref="TerminalOverlayEntry.HeightWeight"/>).</summary>
    public const double DefaultTerminalOverlayHeightWeight = 1.0;
    public const double MinTerminalOverlayHeightWeight = 0.2;
    public const double MaxTerminalOverlayHeightWeight = 5.0;

    /// <summary>Default for <see cref="CommandStackingSeparator"/>.</summary>
    public const string DefaultCommandStackingSeparator = ";";

    /// <summary>Font used for text received from the MUD in the main output view.</summary>
    public string OutputFontFamily { get; set; } = DefaultOutputFontFamily;

    public double OutputFontSize { get; set; } = DefaultOutputFontSize;

    public bool OutputFontBold { get; set; }

    /// <summary>Font shared by all dockable widgets except the terminal.</summary>
    public string WidgetFontFamily { get; set; } = DefaultWidgetFontFamily;

    public double WidgetFontSize { get; set; } = DefaultWidgetFontSize;

    public bool WidgetFontBold { get; set; }

    /// <summary>Palette used for the standard 16 ANSI colors (including indices 0-15).</summary>
    public string TelnetColorScheme { get; set; } = DefaultTelnetColorScheme;

    /// <summary>
    /// Separator character used for command stacking (e.g. ";").
    /// Multiple commands in one text value are split on newlines and on this
    /// separator.  Set to empty to disable stacking (only newlines remain).
    /// Applied to typed commands, alias replacements, trigger actions, and
    /// timer commands.
    /// </summary>
    public string CommandStackingSeparator { get; set; } = DefaultCommandStackingSeparator;

    /// <summary>Last chosen "Tryb mapy" (Proceduralna/Prosta) — restored on the next launch.</summary>
    public MapDisplayMode MapDisplayMode { get; set; } = MapDisplayMode.Procedural;

    /// <summary>Default/limits for <see cref="AutowalkLowMovementThresholdPercent"/>.</summary>
    public const int DefaultAutowalkLowMovementThresholdPercent = 10;
    public const int MinAutowalkLowMovementThresholdPercent = 1;
    public const int MaxAutowalkLowMovementThresholdPercent = 50;

    /// <summary>Movement percentage (of max) at or below which autowalk triggers recovery.</summary>
    public int AutowalkLowMovementThresholdPercent { get; set; } = DefaultAutowalkLowMovementThresholdPercent;

    /// <summary>Default/limits for <see cref="AutowalkRestSeconds"/>.</summary>
    public const int DefaultAutowalkRestSeconds = 30;
    public const int MinAutowalkRestSeconds = 5;
    public const int MaxAutowalkRestSeconds = 300;

    /// <summary>How long autowalk rests (in seconds) before standing back up, when "refresh" isn't
    /// memorized.</summary>
    public int AutowalkRestSeconds { get; set; } = DefaultAutowalkRestSeconds;

    /// <summary>Panels currently pinned as floating overlays on the Terminal, in pin (stacking)
    /// order, each with its relative height weight. Only meaningful in TRANSPARENCY mode — see
    /// <see cref="MudClient.App.Docking.MudDockFactory.IsTransparencyLayout"/> and
    /// <see cref="MudClient.App.Docking.MudDockFactory.OverlayTools"/>.</summary>
    public List<TerminalOverlayEntry> TerminalOverlays { get; set; } = [];

    /// <summary>0 (fully transparent) .. 1 (opaque). Shared by every overlay — lets the terminal
    /// text show through. One setting for all of them, not one per panel.</summary>
    public double TerminalOverlayOpacity { get; set; } = DefaultTerminalOverlayOpacity;

    /// <summary>Plays a short Windows notification sound (see
    /// <see cref="Services.NotificationSoundPlayer"/>) for every line the Chat panel mirrors
    /// (say/sayto/tell/clantell/grouptell/yell/shout — see
    /// <see cref="MudClient.Core.Automation.ChatLinePolicy"/>). Off by default so upgrading
    /// doesn't suddenly start beeping.</summary>
    public bool ChatSoundOnNewMessageEnabled { get; set; }

    /// <summary>Shows the active-timers strip on the terminal's timer/skill-cooldown bar (see
    /// TerminalPanelView). On by default; turn off if the strip feels cluttered with many timers
    /// running.</summary>
    public bool ShowTimersOnStatusBarEnabled { get; set; } = true;

    /// <summary>Shows the enabled-triggers strip (with its gray/blue recently-fired flash) on the
    /// same bar as <see cref="ShowTimersOnStatusBarEnabled"/>. On by default.</summary>
    public bool ShowTriggersOnStatusBarEnabled { get; set; } = true;
}
