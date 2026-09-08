using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;
using MudClient.App.Models;
using MudClient.App.Services;

namespace MudClient.App.Docking;

/// <summary>
/// Generic dockable tool: hosts an arbitrary panel view (<see cref="ViewType"/>)
/// whose DataContext is <see cref="Context"/>. One instance of this class backs
/// every panel in the app (map, room info, terminal, character, automation, ...).
/// </summary>
public sealed class PanelTool : Tool
{
    public required Type ViewType { get; init; }

    /// <summary>Shared contextual help, also rendered in the central Help window.</summary>
    public PanelHelpTopic? HelpTopic => PanelHelpCatalog.Find(Id);

    public bool HasHelpTopic => HelpTopic is not null;

    /// <summary>True for the Map tool specifically — used to pick which settings flyout content
    /// (real map options vs. the generic placeholder) a settings button shows.</summary>
    public bool IsMapTool => string.Equals(Id, "Map", StringComparison.Ordinal);

    // Retained only while old visual templates remain loadable; no Effects tool is registered.
    public bool IsEffectsTool => string.Equals(Id, "Effects", StringComparison.Ordinal);

    /// <summary>True for the Mem tool specifically — its settings are selected by the stable id,
    /// now that its settings flyout also carries the buff-set management moved in from the former
    /// Buffs tool.</summary>
    public bool IsMemTool => string.Equals(Id, "MemSpells", StringComparison.Ordinal);

    /// <summary>True for the Group tool specifically — same purpose as <see cref="IsEffectsTool"/>;
    /// its settings flyout carries the group spell shortcut editor (label -&gt; spell name, shown as
    /// a button next to each member).</summary>
    public bool IsGroupTool => string.Equals(Id, "Group", StringComparison.Ordinal);

    /// <summary>True for the Chat tool specifically — same purpose as <see cref="IsEffectsTool"/>;
    /// its settings flyout carries the "sound on new message" toggle.</summary>
    public bool IsChatTool => string.Equals(Id, "Chat", StringComparison.Ordinal);

    /// <summary>True for the OffensiveActions tool specifically — same purpose as
    /// <see cref="IsEffectsTool"/>; its settings flyout carries the offensive-action and
    /// custom-command shortcut editors.</summary>
    public bool IsOffensiveTool => string.Equals(Id, "OffensiveActions", StringComparison.Ordinal);

    public bool IsStatisticsTool => string.Equals(Id, "Statistics", StringComparison.Ordinal);

    /// <summary>
    /// Set by <see cref="MudDockFactory"/>; moves this tool into a collapsed tab on the
    /// requested edge. Used by the explicit edge choices in panel menus.
    /// </summary>
    internal Action<Alignment>? PinToEdge { get; set; }

    internal Action? ReturnToLayout { get; set; }

    internal Func<bool>? CanReturnToLayout { get; set; }

    /// <summary>Set by <see cref="MudDockFactory"/>; detaches this tool from its dock position
    /// and renders it as a floating, transparent overlay on top of the Terminal panel.</summary>
    internal Action? PinAsOverlay { get; set; }

    internal Func<bool>? CanPinAsOverlay { get; set; }

    /// <summary>Set by <see cref="MudDockFactory"/>; closes this tool's Terminal overlay card
    /// without re-docking it anywhere (see <see cref="MudDockFactory.CloseOverlay"/>).</summary>
    internal Action? CloseOverlay { get; set; }

    internal Func<bool>? CanCloseOverlay { get; set; }

    public PanelTool()
    {
        PinLeftCommand = new RelayCommand(() => PinToEdge?.Invoke(Alignment.Left));
        PinRightCommand = new RelayCommand(() => PinToEdge?.Invoke(Alignment.Right));
        PinTopCommand = new RelayCommand(() => PinToEdge?.Invoke(Alignment.Top));
        PinBottomCommand = new RelayCommand(() => PinToEdge?.Invoke(Alignment.Bottom));
        ReturnToLayoutCommand = new RelayCommand(
            () => ReturnToLayout?.Invoke(),
            () => CanReturnToLayout?.Invoke() == true);
        PinAsOverlayCommand = new RelayCommand(
            () => PinAsOverlay?.Invoke(),
            () => CanPinAsOverlay?.Invoke() == true);
        CloseOverlayCommand = new RelayCommand(
            () => CloseOverlay?.Invoke(),
            () => CanCloseOverlay?.Invoke() == true);
    }

    public IRelayCommand PinLeftCommand { get; }

    public IRelayCommand PinRightCommand { get; }

    public IRelayCommand PinTopCommand { get; }

    public IRelayCommand PinBottomCommand { get; }

    public IRelayCommand ReturnToLayoutCommand { get; }

    public IRelayCommand PinAsOverlayCommand { get; }

    public IRelayCommand CloseOverlayCommand { get; }

    internal void RefreshDockCommands()
    {
        ReturnToLayoutCommand.NotifyCanExecuteChanged();
        PinAsOverlayCommand.NotifyCanExecuteChanged();
        CloseOverlayCommand.NotifyCanExecuteChanged();
    }
}
