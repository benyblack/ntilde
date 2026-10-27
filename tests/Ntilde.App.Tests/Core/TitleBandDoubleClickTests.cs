using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Ntilde.Controls;

namespace Ntilde.Tests.Core;

/// <summary>
/// The top band is the window's own content, not the system's title bar, so Windows never sees a caption double-click
/// there: only focus mode's strip handled one, and double-clicking the normal title band did not maximize.
/// </summary>
public sealed class TitleBandDoubleClickTests : IClassFixture<TestAppDataRoot>, IDisposable
{
    public void Dispose() => TestMainWindowFactory.DisposeCreatedWindows();

    // Further than Avalonia's 4px double-click box, so the next presses start a new double-click.
    private static readonly Point NextDoubleClick = new(10, 0);

    /// <summary>
    /// The empty strip between the last tab and the title-bar items, at the band's middle: what the user grabs to move
    /// the window. Taken from the layout, since tab widths follow the shell's title and differ between machines.
    /// </summary>
    private static Point EmptyBand(MainWindow window)
    {
        double lastTabRight = window.FindControl<TabControl>("Tabs")!.Items.OfType<TabItem>()
            .Max(t => t.TranslatePoint(new Point(t.Bounds.Width, 0), window)!.Value.X);
        double itemsLeft = window.FindControl<StackPanel>("TitleBarItemsHost")!.TranslatePoint(default, window)!.Value.X;
        return new Point((lastTabRight + itemsLeft) / 2, MainWindow.WindowDragBandHeight(false) / 2);
    }

    private static string Describe(MainWindow window, Point at) =>
        $"at {at}: " + string.Join(" < ", ((window.InputHitTest(at) as Visual)?.GetSelfAndVisualAncestors() ?? []).Take(6)
            .Select(v => v.GetType().Name + ((v as Control)?.Name is { Length: > 0 } name ? "#" + name : "")));

    private static void AssertEmptyBand(MainWindow window, Point at)
    {
        var hit = window.InputHitTest(at) as Visual;
        Assert.NotNull(hit);
        Assert.False(hit!.GetSelfAndVisualAncestors().Any(v => v is Button or TabItem or TerminalPane), Describe(window, at));
    }

    /// <summary>Every left press the window routes, with its click count and who handled it, for failure messages.</summary>
    private static List<string> RecordPresses(MainWindow window)
    {
        var presses = new List<string>();
        window.AddHandler(InputElement.PointerPressedEvent,
            (_, e) => presses.Add($"press {e.ClickCount} on {(e.Source as Control)?.GetType().Name}, handled={e.Handled}, state={window.WindowState}"),
            RoutingStrategies.Bubble, handledEventsToo: true);
        return presses;
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

    private static void AssertState(WindowState expected, MainWindow window, Point at, List<string> presses) =>
        Assert.True(window.WindowState == expected,
            $"expected {expected}, was {window.WindowState}; {Describe(window, at)}\n{string.Join("\n", presses)}");

    [AvaloniaFact]
    public void Double_clicking_the_empty_title_band_maximizes_and_restores_the_window()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        Point at = EmptyBand(window);
        AssertEmptyBand(window, at);
        AssertEmptyBand(window, at + NextDoubleClick);
        var presses = RecordPresses(window);

        DoubleClick(window, at);
        AssertState(WindowState.Maximized, window, at, presses);

        // The maximized window is wider, but the strip only grows to the right.
        DoubleClick(window, at + NextDoubleClick);
        AssertState(WindowState.Normal, window, at + NextDoubleClick, presses);
    }

    [AvaloniaFact]
    public void A_single_click_on_the_title_band_leaves_the_window_state_alone()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        Point at = EmptyBand(window);
        AssertEmptyBand(window, at);

        Press(window, at);

        Assert.Equal(WindowState.Normal, window.WindowState);
    }

    [AvaloniaFact]
    public void A_double_click_on_the_focus_strip_maximizes_once()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        window.EnterFocusMode();
        Dispatcher.UIThread.RunJobs();
        var at = new Point(300, MainWindow.FocusStripHeight / 2);
        AssertEmptyBand(window, at);
        var presses = RecordPresses(window);

        DoubleClick(window, at);
        AssertState(WindowState.Maximized, window, at, presses);

        DoubleClick(window, at + NextDoubleClick);
        AssertState(WindowState.Normal, window, at + NextDoubleClick, presses);
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
