using MudClient.App.Models;

namespace MudClient.App.Docking;

/// <summary>Serializable snapshot of the dock tree, keyed by the stable <see cref="PanelTool"/> ids.</summary>
public sealed class DockLayoutSnapshot
{
    public DockNodeSnapshot? Root { get; set; }

    public List<string> HiddenToolIds { get; set; } = new();

    /// <summary>Tools the user has auto-hidden (pinned to an edge). These live outside the
    /// visible tree in the root's pinned collections, so they are tracked separately.</summary>
    public List<PinnedToolSnapshot> PinnedTools { get; set; } = new();

    /// <summary>Whether this snapshot was taken while <see cref="MudClient.App.Docking.MudDockFactory.IsTransparencyLayout"/>
    /// was active, so restoring it (e.g. on app restart) re-enables pinning panels as overlays.</summary>
    public bool IsTransparencyLayout { get; set; }

    /// <summary>Overlay arrangement (which panels, columns, order, sizes) at the moment this
    /// snapshot was taken — only meaningful when <see cref="IsTransparencyLayout"/> is true. The
    /// dock tree itself has no idea overlays exist (see <c>MudDockFactory.Snapshot</c>'s own
    /// remarks) — the live overlay arrangement actually lives in <c>AppSettings.TerminalOverlays</c>,
    /// so without a copy here, restoring this snapshot would silently reuse whatever the *current*
    /// live settings happen to hold instead of what was true when this was saved. Empty in
    /// snapshots taken before this was tracked, or that were never in TRANSPARENCY mode.</summary>
    public List<TerminalOverlayEntry> TerminalOverlays { get; set; } = new();

    /// <summary>Shared overlay transparency (see <c>AppSettings.TerminalOverlayOpacity</c>) at the
    /// moment this snapshot was taken. Null in snapshots saved before this was tracked.</summary>
    public double? TerminalOverlayOpacity { get; set; }
}

/// <summary>An auto-hidden (pinned) tool and the tool dock it should snap back to.</summary>
public sealed class PinnedToolSnapshot
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Id of the ToolDock the tool snaps back to when un-hidden.</summary>
    public string? OwnerId { get; set; }

    /// <summary>Screen edge the tab sits on ("Left"/"Right"/"Top"/"Bottom"). Null in snapshots
    /// saved before per-edge tabs existed — those fall back to the owner dock's alignment.</summary>
    public string? Edge { get; set; }
}

public sealed class DockNodeSnapshot
{
    /// <summary>"Root" | "Proportional" | "ToolDock" | "Splitter" | "Panel".</summary>
    public string Kind { get; set; } = string.Empty;

    public string? Id { get; set; }

    public double Proportion { get; set; } = double.NaN;

    /// <summary>Orientation.ToString(), only set for "Proportional" nodes.</summary>
    public string? Orientation { get; set; }

    /// <summary>Id of the active child, only set for "ToolDock"/"Root" nodes.</summary>
    public string? ActiveDockableId { get; set; }

    public List<DockNodeSnapshot> Children { get; set; } = new();
}
