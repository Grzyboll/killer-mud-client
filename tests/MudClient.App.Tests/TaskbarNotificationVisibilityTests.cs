using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MudClient.App.Services;
using MudClient.App.ViewModels;
using MudClient.App.Views;
using Xunit;

namespace MudClient.App.Tests;

/// <summary>
/// Regression coverage: the taskbar-overlay notification badges (see
/// <see cref="MainWindow.IsHiddenFromView"/> / TaskbarOverlayIconService) must arm only while the
/// window is minimized or not the OS-focused window — never while it's open and actually being
/// looked at. Minimizing doesn't reliably raise Avalonia's Activated/Deactivated on every
/// platform, which is exactly the gap <see cref="MainWindow.IsHiddenFromView"/> and its
/// WindowState-change handler close: IsActive alone isn't trustworthy for the "minimized" case.
/// </summary>
[Collection(AvaloniaUiCollection.Name)]
public sealed class TaskbarNotificationVisibilityTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(), "KillerMudClient-TaskbarNotificationVisibilityTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_tempDirectory))
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
    }

    private MainWindowViewModel CreateViewModel() => new(
        new ProfileService(_tempDirectory),
        new AppSettingsService(_tempDirectory),
        new DockLayoutService(_tempDirectory));

    private static bool GetIsHiddenFromView(MainWindow window) =>
        (bool)typeof(MainWindow)
            .GetProperty("IsHiddenFromView", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    private static void SetIsFighting(MainWindow window, bool value) =>
        typeof(MainWindow).GetField("_isFighting", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(window, value);

    private static bool GetRedNotificationActive(MainWindow window) =>
        (bool)typeof(MainWindow)
            .GetField("_redNotificationActive", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(window)!;

    [AvaloniaFact]
    public void IsHiddenFromView_WindowShownAndActivated_ReturnsFalse()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();

            Assert.False(GetIsHiddenFromView(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void IsHiddenFromView_WindowMinimized_ReturnsTrueRegardlessOfIsActive()
    {
        // The exact gap this exists for: some platforms keep reporting IsActive == true for a
        // minimized window (it's still the OS's last-active window even though it isn't visible),
        // so WindowState must be checked explicitly rather than trusting IsActive alone.
        var window = new MainWindow { DataContext = CreateViewModel() };
        try
        {
            window.Show();

            window.WindowState = WindowState.Minimized;

            Assert.True(GetIsHiddenFromView(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Minimizing_MidFight_ArmsRedNotification()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        try
        {
            window.Show();
            SetIsFighting(window, true);
            Assert.False(GetRedNotificationActive(window));

            window.WindowState = WindowState.Minimized;

            Assert.True(GetRedNotificationActive(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Minimizing_WithoutAFight_DoesNotArmRedNotification()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        try
        {
            window.Show();

            window.WindowState = WindowState.Minimized;

            Assert.False(GetRedNotificationActive(window));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void RestoringWhileActive_ClearsRedNotification()
    {
        var window = new MainWindow { DataContext = CreateViewModel() };
        try
        {
            window.Show();
            window.Activate();
            Dispatcher.UIThread.RunJobs();
            SetIsFighting(window, true);
            window.WindowState = WindowState.Minimized;
            Assert.True(GetRedNotificationActive(window));

            window.WindowState = WindowState.Normal;
            Dispatcher.UIThread.RunJobs();

            Assert.False(GetRedNotificationActive(window));
        }
        finally
        {
            window.Close();
        }
    }
}
