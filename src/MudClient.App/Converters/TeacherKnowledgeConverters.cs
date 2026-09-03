using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MudClient.App.Controls;
using MudClient.App.Services;

namespace MudClient.App.Converters;

/// <summary>Strikes through a teacher skill's name when it's <see cref="SkillKnowledgeState.NotLearnable"/>
/// (outside this character's class) — the same treatment as <see cref="WorldMapControl.CreateSkillRun"/>.
/// Unlike <see cref="WorldMapControl"/>'s own teacher tooltip, the Killeropedia Nauczyciele view
/// doesn't also color the text green/gold/gray — those colors were unreadable against this view's
/// background, so it stays the theme's default (black-ish) foreground and relies on strikethrough
/// alone to flag "not learnable".</summary>
public sealed class SkillKnowledgeStateStrikethroughConverter : IValueConverter
{
    public static readonly SkillKnowledgeStateStrikethroughConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is SkillKnowledgeState.NotLearnable ? TextDecorations.Strikethrough : null;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
