using MudClient.Core.Automation;

namespace MudClient.Core.Tests;

public sealed class RemoteCommandPolicyTests
{
    [Fact]
    public void TryGetCommand_TrustedCharacterSaysPrefixedLine_ReturnsCommandWithoutPrefix()
    {
        var matched = RemoteCommandPolicy.TryGetCommand(
            "Gandalf mówi: '!stand'.", "Gandalf", out var command);

        Assert.True(matched);
        Assert.Equal("stand", command);
    }

    [Fact]
    public void TryGetCommand_IsCaseInsensitiveWhenCheckingTrustedName()
    {
        var matched = RemoteCommandPolicy.TryGetCommand(
            "GANDALF mówi: '!wstan'.", "gandalf", out var command);

        Assert.True(matched);
        Assert.Equal("wstan", command);
    }

    [Theory]
    [InlineData("Obcy mówi: '!stand'.")] // wrong speaker
    [InlineData("Gandalf mówi: 'stand'.")] // no "!" prefix — plain chat, not a command
    [InlineData("Gandalf pyta: '!stand'.")] // wrong verb (not say)
    public void TryGetCommand_WrongSpeakerVerbOrNoPrefix_ReturnsFalse(string line)
    {
        Assert.False(RemoteCommandPolicy.TryGetCommand(line, "Gandalf", out var command));
        Assert.Equal(string.Empty, command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryGetCommand_NoTrustedNameConfigured_ReturnsFalse(string? trustedName)
    {
        Assert.False(RemoteCommandPolicy.TryGetCommand("Gandalf mówi: '!stand'.", trustedName, out _));
    }

    [Fact]
    public void TryGetCommand_PrefixOnlyWithNothingAfter_ReturnsFalse()
    {
        Assert.False(RemoteCommandPolicy.TryGetCommand("Gandalf mówi: '!'.", "Gandalf", out _));
    }

    [Fact]
    public void TryGetCommand_TrimsSurroundingWhitespaceFromCommand()
    {
        var matched = RemoteCommandPolicy.TryGetCommand(
            "Gandalf mówi: '!  stand  '.", "Gandalf", out var command);

        Assert.True(matched);
        Assert.Equal("stand", command);
    }
}
