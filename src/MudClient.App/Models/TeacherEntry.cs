using MudClient.App.Services;

namespace MudClient.App.Models;

public sealed record TeacherSkillEntry(
    string Name,
    int Min,
    int? Max,
    int RequiredSkill,
    int Price)
{
    public string RangeText => Max is null ? $"od {Min}" : $"{Min}–{Max}";

    public string PriceText => $"{Price}%";

    public string RequirementText => $"od {RequiredSkill}";
}

/// <summary>One skill-percent threshold a trick needs — e.g. "kick na min. 85%".</summary>
public sealed record TrickRequirement(string SkillName, int MinPercent)
{
    public string Text => $"{SkillName} na min. {MinPercent}%";
}

/// <summary>
/// A trick a teacher offers, enriched (where known) with the description from
/// https://killer.fandom.com/pl/wiki/Sztuczki_(tricks) — everything past <see cref="Price"/> is
/// optional since it was hand-curated only for tricks actually taught in-game.
/// </summary>
public sealed record TeacherTrickEntry(
    string Name,
    int LearnChance,
    int Price,
    string? EnhancesText = null,
    IReadOnlyList<string>? AvailableClasses = null,
    IReadOnlyList<TrickRequirement>? Requirements = null,
    bool RequiresAllRequirements = true,
    string? ActivationText = null)
{
    public string LearnChanceText => $"{LearnChance}%";

    public string PriceText => $"{Price} $";

    public bool HasDescription =>
        EnhancesText is not null || Requirements is { Count: > 0 } || ActivationText is not null;

    public string AvailableClassesText =>
        AvailableClasses is { Count: > 0 } ? string.Join(", ", AvailableClasses) : "brak danych";

    public string RequirementsText => Requirements is not { Count: > 0 }
        ? "brak danych"
        : string.Join(RequiresAllRequirements ? " oraz " : " lub ", Requirements.Select(r => r.Text));

    /// <summary>One readable paragraph combining everything transcribed from the wiki, for the
    /// Killeropedia "Triki" detail panel — mirrors <c>TattooBonusEntry.Description</c>'s role.</summary>
    public string DescriptionText
    {
        get
        {
            if (!HasDescription)
            {
                return "Brak szczegółowych informacji o tym triku.";
            }

            var parts = new List<string>();
            if (EnhancesText is not null)
            {
                parts.Add($"Wzmacnia: {EnhancesText}.");
            }

            if (AvailableClasses is { Count: > 0 })
            {
                parts.Add($"Dostępne dla: {AvailableClassesText}.");
            }

            if (Requirements is { Count: > 0 })
            {
                parts.Add($"Wymaga: {RequirementsText}.");
            }

            if (ActivationText is not null)
            {
                parts.Add($"Szansa uruchomienia: {ActivationText}.");
            }

            return string.Join(" ", parts);
        }
    }
}

public sealed record TeacherEntry(
    string MobVnum,
    string Name,
    string Region,
    string? Area,
    string? RoomVnum,
    IReadOnlyList<string> Classes,
    IReadOnlyList<TeacherSkillEntry> Skills,
    IReadOnlyList<TeacherTrickEntry> Tricks)
{
    public string LocationText => string.IsNullOrWhiteSpace(Area) ? Region : Area;

    public string RegionText => string.IsNullOrWhiteSpace(Area) || Area == Region
        ? Region
        : $"{Area} · region {Region}";

    public string RoomText => string.IsNullOrWhiteSpace(RoomVnum) ? "brak danych" : RoomVnum;

    public bool HasRoomLocation => !string.IsNullOrWhiteSpace(RoomVnum);

    public string ClassesText => Classes.Count == 0 ? "brak danych" : string.Join(", ", Classes);

    public string OfferingCountText => $"{Skills.Count} umiejętności · {Tricks.Count} trików";
}

/// <summary>One (teacher, skill) pair — the flat "Umiejętności" sub-tab in Killeropedia's
/// Nauczyciele view lists one row per skill a teacher offers, instead of grouping by teacher
/// first, so e.g. "who teaches kick" is a single scan instead of clicking through every
/// teacher. <see cref="State"/> is baked in (recomputed whenever the search/filter or
/// <see cref="ViewModels.KilleropediaViewModel.SkillKnowledge"/> changes — see
/// <c>KilleropediaViewModel.ApplyTeacherFilter</c>) rather than bound live, since Avalonia's
/// MultiBinding can't target a collection-typed property like <c>TextBlock.TextDecorations</c>,
/// so the coloring converters take this plain enum instead of (skill, knowledge) directly.</summary>
public sealed record TeacherSkillRow(TeacherEntry Teacher, TeacherSkillEntry Skill, SkillKnowledgeState State);

/// <summary>One (teacher, trick) pair — the flat "Triki" sub-tab counterpart to
/// <see cref="TeacherSkillRow"/>. <see cref="IsLearnable"/> mirrors
/// <see cref="TrickKnowledgeClassifier.MeetsRequirements"/>, baked in the same way and for the
/// same reason as <see cref="TeacherSkillRow.State"/>.</summary>
public sealed record TeacherTrickRow(TeacherEntry Teacher, TeacherTrickEntry Trick, bool IsLearnable);

/// <summary>Skill counterpart shown in the "Wg nauczyciela" tab's per-teacher detail pane — same
/// <see cref="TeacherSkillRow.State"/> idea, minus the teacher (already the whole pane's scope).</summary>
public sealed record TeacherSkillOffering(TeacherSkillEntry Skill, SkillKnowledgeState State);

/// <summary>Trick counterpart to <see cref="TeacherSkillOffering"/>.</summary>
public sealed record TeacherTrickOffering(TeacherTrickEntry Trick, bool IsLearnable);
