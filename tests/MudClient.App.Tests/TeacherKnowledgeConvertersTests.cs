using System.Globalization;
using Avalonia.Media;
using MudClient.App.Converters;
using MudClient.App.Services;

namespace MudClient.App.Tests;

/// <summary>Covers the Killeropedia Nauczyciele skill strikethrough converter — the only
/// knowledge-state visual treatment left in that view after its foreground coloring was dropped
/// for being unreadable against the view's background (see the converter's own doc comment).</summary>
public sealed class TeacherKnowledgeConvertersTests
{
    [Fact]
    public void SkillKnowledgeStateStrikethroughConverter_NotLearnable_ReturnsStrikethrough()
    {
        var result = SkillKnowledgeStateStrikethroughConverter.Instance.Convert(
            SkillKnowledgeState.NotLearnable, typeof(TextDecorationCollection), null, CultureInfo.InvariantCulture);

        Assert.Same(TextDecorations.Strikethrough, result);
    }

    [Theory]
    [InlineData(SkillKnowledgeState.Known)]
    [InlineData(SkillKnowledgeState.Learnable)]
    [InlineData(SkillKnowledgeState.Unknown)]
    public void SkillKnowledgeStateStrikethroughConverter_EverythingElse_ReturnsNull(SkillKnowledgeState state)
    {
        var result = SkillKnowledgeStateStrikethroughConverter.Instance.Convert(
            state, typeof(TextDecorationCollection), null, CultureInfo.InvariantCulture);

        Assert.Null(result);
    }
}
