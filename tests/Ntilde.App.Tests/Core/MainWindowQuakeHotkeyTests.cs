using System;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Xunit;

namespace Ntilde.Tests.Core;

/// <summary>
/// Quake mode toggles the window with Hide()/Show(), and Avalonia re-raises OnOpened on every
/// Show() that follows a Hide(). GlobalHotkey subclasses the window's WNDPROC and chains to
/// whatever was installed before it, so a second instance built by that re-run would leave the
/// first one's thunk in the chain with nothing keeping it alive - see MainWindow.OnOpened. There
/// is no HWND under headless, so the hotkey itself is inert here; what this pins is that the
/// window never builds a second one.
/// </summary>
public sealed class MainWindowQuakeHotkeyTests : IDisposable
{
    /// <summary>
    /// Disposes the panes of every window this class asked for, and with them the real shells
    /// behind them. xUnit builds a fresh instance per test, so this runs after each one.
    /// </summary>
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    [AvaloniaFact]
    public void HideShowRoundTrip_ReRaisesOnOpened_ButKeepsTheOneGlobalHotkey()
    {
        // Explicit rather than leaning on the TerminalSettings default, which is what this
        // test's meaning depends on.
        AppServiceBundle services = AppServices.BuildForDesigner() with
        {
            Settings = new TerminalSettings { QuakeModeEnabled = true }
        };
        var window = TestMainWindowFactory.Create(services);
        int openedCount = 0;
        window.Opened += (_, _) => openedCount++;

        window.Show();
        Dispatcher.UIThread.RunJobs();

        var first = window.GlobalHotkeyForTest;
        Assert.NotNull(first);

        // The round trip ToggleVisibility performs on the hotkey.
        window.Hide();
        Dispatcher.UIThread.RunJobs();
        window.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.True(
            openedCount == 2,
            $"Expected Show() after Hide() to re-raise OnOpened (Opened fired {openedCount} time(s)); " +
            "without that re-run this test cannot tell a guarded OnOpened from an unguarded one.");
        Assert.Same(first, window.GlobalHotkeyForTest);
    }
}
