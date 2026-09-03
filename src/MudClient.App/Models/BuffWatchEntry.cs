using CommunityToolkit.Mvvm.ComponentModel;

namespace MudClient.App.Models;

/// <summary>
/// A buff the user wants to keep active, matched by name against
/// Char.Affects GMCP entries. Stored per profile.
/// </summary>
public sealed partial class BuffWatchEntry : ObservableObject
{
    public BuffWatchEntry(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Affect name as typed by the user; also used as the spell name
    /// in the recast command.
    /// </summary>
    public string Name { get; }

    /// <summary>True when the buff is present in the latest Char.Affects.</summary>
    [ObservableProperty]
    private bool _isActive;

    /// <summary>True when this spell is currently memorized (Char.MemSpell reports it Memed, not
    /// just Meming) — i.e. ready to cast right now without a "mem" command first. Kept in sync
    /// from <see cref="ViewModels.MainWindowViewModel"/>'s own memorized-spells tracking whenever
    /// Char.MemSpell updates, the same source <see cref="ViewModels.MainWindowViewModel.MemorizedSpellNames"/>
    /// uses.</summary>
    [ObservableProperty]
    private bool _isMemorized;

    /// <summary>This spell's magic circle, or null when it isn't known yet (no Char.MemSpell entry
    /// for this name has been seen this session).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CircleDisplay))]
    [NotifyPropertyChangedFor(nameof(HasCircle))]
    private int? _circle;

    /// <summary>"Krąg 3", or empty when <see cref="Circle"/> isn't known yet.</summary>
    public string CircleDisplay => Circle is { } circle ? $"Krąg {circle}" : string.Empty;

    public bool HasCircle => Circle is not null;

    /// <summary>
    /// Normalizes an affect name for comparison: the server appends a
    /// parenthesized counter to some affects (e.g. "mirror image (7)"),
    /// which must be ignored when matching against the user's list.
    /// </summary>
    public static string NormalizeName(string name)
    {
        var open = name.IndexOf('(');
        if (open >= 0)
        {
            name = name[..open];
        }

        return name.Trim();
    }
}
