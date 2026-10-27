using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Ntilde.Tests.Core;

/// <summary>
/// The top band is the window's own content, not the system's title bar, so Windows never sees a caption double-click
/// there: only focus mode's strip handled one, and double-clicking the normal title band did not maximize.
/// </summary>
public sealed class TitleBandDoubleClickTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    // Empty tab strip, left of the title-bar items: what the user grabs to move the window.
    private static readonly Point EmptyBand = new(300, 18);

    private static void AssertEmptyBand(MainWindow window, Point at)
    {
        var hit = window.InputHitTest(at) as Visual;
        Assert.NotNull(hit);
        Assert.DoesNotContain(hit!.GetSelfAndVisualAncestors(), v => v is Button or TabItem);
    }

    private static void Press(MainWindow window, Point at)
    {
        window.MouseDown(at, MouseButton.Left);
        window.MouseUp(at, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void DoubleClick(MainWindow window, Point at)
    {
        Press(window, at);
        Press(window, at);
    }

    [AvaloniaFact]
    public void Double_clicking_the_empty_title_band_maximizes_and_restores_the_window()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        AssertEmptyBand(window, EmptyBand);

        DoubleClick(window, EmptyBand);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        // Elsewhere on the band, so the presses count as a new double-click rather than clicks three and four.
        DoubleClick(window, EmptyBand + new Point(80, 0));
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void A_single_click_on_the_title_band_leaves_the_window_state_alone()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();

        Press(window, EmptyBand);

        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void A_double_click_on_the_focus_strip_maximizes_once()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();
        Dispatcher.UIThread.RunJobs();
        var inStrip = new Point(300, MainWindow.FocusStripHeight / 2);

        DoubleClick(window, inStrip);
        Assert.Equal(WindowState.Maximized, window.WindowState);

        DoubleClick(window, inStrip + new Point(80, 0));
        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void A_double_click_below_the_title_band_does_not_maximize()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        var below = new Point(300, MainWindow.WindowDragBandHeight(false) + 40);

        DoubleClick(window, below);

        Assert.Equal(WindowState.Normal, window.WindowState);
    }
}
