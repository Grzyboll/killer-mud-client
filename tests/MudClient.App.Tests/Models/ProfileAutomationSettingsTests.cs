using MudClient.App.Models;

namespace MudClient.App.Tests.Models;

public sealed class ProfileAutomationSettingsTests
{
    [Fact]
    public void Constructor_OutputWordWrapIsEnabledByDefault()
    {
        Assert.True(new ProfileAutomationSettings().OutputWordWrap);
    }

    [Fact]
    public void Constructor_ClearCommandInputAfterSendIsDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().ClearCommandInputAfterSend);
    }

    [Fact]
    public void Constructor_AutoAssistIsDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().AutoAssistEnabled);
    }

    [Fact]
    public void Constructor_AutoAssistExclusionsAreEmptyByDefault()
    {
        Assert.Empty(new ProfileAutomationSettings().AutoAssistExcludedMobNames);
    }

    [Fact]
    public void Constructor_AutoAssistFollowUpCommandsAreEmptyByDefault()
    {
        Assert.Empty(new ProfileAutomationSettings().AutoAssistFollowUpCommands);
    }

    [Fact]
    public void Constructor_GroupOrdersAreDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().GroupOrdersEnabled);
    }

    [Fact]
    public void Constructor_AutoRecastOnLeaderSnapIsDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().AutoRecastOnLeaderSnapEnabled);
    }

    [Fact]
    public void Constructor_RemoteControlIsDisabledByDefaultWithNoCharacterName()
    {
        var settings = new ProfileAutomationSettings();

        Assert.False(settings.RemoteControlEnabled);
        Assert.Equal(string.Empty, settings.RemoteControlCharacterName);
    }

    [Fact]
    public void Constructor_AutoRecastOnLeaderSnapCommandsDefaultToRecast()
    {
        Assert.Equal("/recast", new ProfileAutomationSettings().AutoRecastOnLeaderSnapCommandsText);
    }

    [Fact]
    public void Constructor_NumberedGroupMapMarkersAreDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().ShowGroupMembersAsNumbers);
    }

    [Fact]
    public void Constructor_LordModeIsDisabledByDefault()
    {
        Assert.False(new ProfileAutomationSettings().LordModeEnabled);
    }

    [Fact]
    public void Constructor_AutoKillMobNamesAreEmptyByDefault()
    {
        Assert.Empty(new ProfileAutomationSettings().AutoKillMobNames);
    }

    [Fact]
    public void Constructor_AutowieldWeaponNameIsEmptyByDefault()
    {
        Assert.Empty(new ProfileAutomationSettings().AutowieldWeaponName);
    }
}
