using CommunityToolkit.Mvvm.ComponentModel;

namespace MudClient.App.Models;

/// <summary>
/// Alias or trigger shown in the UI. Pattern is a .NET regex; Action may use
/// capture-group substitutions like $1.
/// </summary>
public sealed class AutomationRuleEntry : ObservableObject, IActivatableFolderItem
{
    private string _name;
    private string _type;
    private string _pattern;
    private string _action;
    private bool _isScript;
    private bool _isEnabled;
    private bool _isGlobal;
    private string? _folderId;
    private bool _isEditing;
    private bool _playSoundOnMatch;
    private bool _recentlyFired;

    public AutomationRuleEntry(
        string name, string type, string pattern, string action, bool isEnabled,
        bool isGlobal = false, bool isScript = false, bool playSoundOnMatch = false)
    {
        _name = name;
        _type = type;
        _pattern = pattern;
        _action = action;
        _isEnabled = isEnabled;
        _isGlobal = isGlobal;
        _isScript = isScript;
        _playSoundOnMatch = playSoundOnMatch;
    }

    /// <summary>Stable identity across renames/edits — what multibox merging keys on (see
    /// <c>RuleMergeKey</c>) instead of Type+Name, which two independently created rules can
    /// share by coincidence.</summary>
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value);
    }

    /// <summary>"alias" or "trigger" ("timer" kept for legacy profiles).</summary>
    public string Type
    {
        get => _type;
        set => SetProperty(ref _type, value);
    }

    public string Pattern
    {
        get => _pattern;
        set
        {
            if (SetProperty(ref _pattern, value))
            {
                OnPropertyChanged(nameof(PatternAndActionSummary));
            }
        }
    }

    /// <summary>A "$1"-style replacement/command template when <see cref="IsScript"/> is false;
    /// Lua source when it's true.</summary>
    public string Action
    {
        get => _action;
        set
        {
            if (SetProperty(ref _action, value))
            {
                OnPropertyChanged(nameof(PatternAndActionSummary));
            }
        }
    }

    /// <summary>Pattern + Action combined into one string — used as a tooltip in the compact
    /// list view (see <c>MainWindowViewModel.IsAutomationCompactView</c>), where the full detail
    /// grid is hidden to keep the list scannable.</summary>
    public string PatternAndActionSummary => $"Wzorzec: {Pattern}\nAkcja: {Action}";

    /// <summary>True when <see cref="Action"/> is Lua source instead of a replacement/command
    /// template.</summary>
    public bool IsScript
    {
        get => _isScript;
        set => SetProperty(ref _isScript, value);
    }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (SetProperty(ref _isEnabled, value))
            {
                OnPropertyChanged(nameof(StatusText));
            }
        }
    }

    public string StatusText => IsEnabled ? "WŁĄCZONY" : "WYŁĄCZONY";

    /// <summary>Trigger-only: plays a short notification sound (see
    /// <see cref="Services.NotificationSoundPlayer"/>) every time this rule's pattern matches an
    /// incoming line — independent of the Chat panel's own "sound on new message" setting, which
    /// only covers chat lines. Ignored for aliases (they fire on typed input, not server output,
    /// so there's nothing to be notified about).</summary>
    public bool PlaySoundOnMatch
    {
        get => _playSoundOnMatch;
        set => SetProperty(ref _playSoundOnMatch, value);
    }

    /// <summary>True = shared by all profiles (stored in the global file).</summary>
    public bool IsGlobal
    {
        get => _isGlobal;
        set => SetProperty(ref _isGlobal, value);
    }

    /// <summary>Id of the containing folder, or null when loose.</summary>
    public string? FolderId
    {
        get => _folderId;
        set => SetProperty(ref _folderId, value);
    }

    /// <summary>UI-only (never persisted) — true while this rule's inline editor is expanded
    /// under its row in the Aliasy/Triggery tab. Set by <see cref="MainWindowViewModel"/>'s
    /// edit-rule flow, at most one entry at a time.</summary>
    public bool IsEditing
    {
        get => _isEditing;
        set => SetProperty(ref _isEditing, value);
    }

    /// <summary>UI-only (never persisted) — true for a few seconds right after this trigger's
    /// pattern matches a line, driving the "recently fired" highlight on the timers/triggers
    /// status bar (see <see cref="Views.Panels.TerminalPanelView"/>). Only ever set for triggers
    /// — aliases fire on typed input, not received text, so there's nothing to flash for.</summary>
    public bool RecentlyFired
    {
        get => _recentlyFired;
        set => SetProperty(ref _recentlyFired, value);
    }
}
