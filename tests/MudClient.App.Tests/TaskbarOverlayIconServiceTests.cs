using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using MudClient.App.Services;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// TaskbarOverlayIconService is a no-op on every platform except Windows (see its own doc
/// comment). These just prove SetState never throws for any color, including combined flags — on
/// non-Windows CI that's trivially true; on Windows it exercises the real COM/GDI32 interop
/// against a headless window's platform handle (which may itself be unavailable, or there may be
/// no Explorer taskbar to talk to, in which case the service quietly no-ops).
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class TaskbarOverlayIconServiceTests
{
    [AvaloniaTheory]
    [InlineData(TaskbarNotificationColor.None)]
    [InlineData(TaskbarNotificationColor.Red)]
    [InlineData(TaskbarNotificationColor.Green)]
    [InlineData(TaskbarNotificationColor.Blue)]
    [InlineData(TaskbarNotificationColor.Red | TaskbarNotificationColor.Green)]
    [InlineData(TaskbarNotificationColor.Red | TaskbarNotificationColor.Blue)]
    [InlineData(TaskbarNotificationColor.Green | TaskbarNotificationColor.Blue)]
    [InlineData(TaskbarNotificationColor.Red | TaskbarNotificationColor.Green | TaskbarNotificationColor.Blue)]
    public void SetState_AnyColorCombination_DoesNotThrow(TaskbarNotificationColor color)
    {
        RunOnHeadlessWindow(window =>
        {
            var exception = Record.Exception(() => TaskbarOverlayIconService.SetState(window, color));
            Assert.Null(exception);
        });
    }

    [AvaloniaFact]
    public void SetState_SameColorTwice_ReusesCachedIconWithoutThrowing()
    {
        // The icon cache keyed by color combination must tolerate being asked for the same
        // combination repeatedly (e.g. every automation firing while blue is already lit).
        RunOnHeadlessWindow(window =>
        {
            var exception = Record.Exception(() =>
            {
                TaskbarOverlayIconService.SetState(window, TaskbarNotificationColor.Blue);
                TaskbarOverlayIconService.SetState(window, TaskbarNotificationColor.Blue);
            });
            Assert.Null(exception);
        });
    }

    [AvaloniaFact]
    public void SetState_ClearAfterColor_DoesNotThrow()
    {
        RunOnHeadlessWindow(window =>
        {
            var exception = Record.Exception(() =>
            {
                TaskbarOverlayIconService.SetState(window, TaskbarNotificationColor.Red);
                TaskbarOverlayIconService.SetState(window, TaskbarNotificationColor.None);
            });
            Assert.Null(exception);
        });
    }

    [AvaloniaFact]
    public void SetState_UnopenedWindowWithNoPlatformHandle_DoesNotThrow()
    {
        var window = new Window();

        var exception = Record.Exception(
            () => TaskbarOverlayIconService.SetState(window, TaskbarNotificationColor.Green));

        Assert.Null(exception);
    }

    private static void RunOnHeadlessWindow(Action<Window> action)
    {
        var window = new Window();
        window.Show();

        try
        {
            action(window);
        }
        finally
        {
            window.Close();
        }
    }
}
