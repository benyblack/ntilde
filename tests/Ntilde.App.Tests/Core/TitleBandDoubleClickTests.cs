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

    // PR #605 review: the band ends where the content starts, so the terminal's first row is not part of it.
    [AvaloniaFact]
    public void A_double_click_on_the_terminals_first_row_does_not_maximize()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        var firstRow = new Point(window.Bounds.Width / 2, MainWindow.WindowDragBandHeight(false));
        Assert.True(((window.InputHitTest(firstRow) as Visual)?.GetSelfAndVisualAncestors() ?? []).Any(v => v is TerminalPane),
            Describe(window, firstRow));
        var presses = RecordPresses(window);

        DoubleClick(window, firstRow);

        AssertState(WindowState.Normal, window, firstRow, presses);
    }

    // PR #605 review: Avalonia counts a double-click by the point in the window, and a drag moves the window under the
    // pointer, so grabbing the window again right after a quick drag looked like a double-click on the band.
    [AvaloniaFact]
    public void Grabbing_the_band_again_after_the_window_moved_is_not_a_double_click()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        Point at = EmptyBand(window);
        AssertEmptyBand(window, at);
        var presses = RecordPresses(window);

        Press(window, at);
        window.Position = new PixelPoint(window.Position.X + 120, window.Position.Y + 80); // the drag that press started
        Press(window, at);

        Assert.Contains(presses, p => p.StartsWith("press 2", StringComparison.Ordinal)); // Avalonia did count a double-click
        AssertState(WindowState.Normal, window, at, presses);
    }

    // The first press of every double-click starts a drag, which may nudge the window a pixel: the check on the screen
    // allows the same few pixels as Avalonia's own in the window, so that double-click still counts.
    [AvaloniaFact]
    public void A_double_click_whose_first_press_nudged_the_window_still_maximizes()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        Point at = EmptyBand(window);
        AssertEmptyBand(window, at);
        var presses = RecordPresses(window);

        Press(window, at);
        window.Position = new PixelPoint(window.Position.X + 1, window.Position.Y + 1);
        Press(window, at);

        AssertState(WindowState.Maximized, window, at, presses);
    }

    // PR #608 review: a finger's double-tap box is far larger than a mouse's, so the check on the screen must use the
    // press's own pointer type, or a double-tap whose taps land a few pixels apart drags instead of maximizing.
    [AvaloniaFact]
    public void A_touch_double_tap_whose_taps_land_apart_maximizes()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        Point at = EmptyBand(window);
        var secondTap = at + new Point(10, 0); // outside a mouse's 4px box, inside a finger's
        AssertEmptyBand(window, at);
        AssertEmptyBand(window, secondTap);
        var presses = RecordPresses(window);

        Tap(window, at);
        Tap(window, secondTap);

        Assert.Contains(presses, p => p.StartsWith("press 2", StringComparison.Ordinal)); // Avalonia did count a double-tap
        AssertState(WindowState.Maximized, window, secondTap, presses);
    }

    private static void Tap(MainWindow window, Point at)
    {
        using var touch = window.TouchBegin(at);
        window.TouchEnd(touch, at);
        Dispatcher.UIThread.RunJobs();
    }

    // PR #605 review: the title bar's own handler and the window's both see a press on the title bar's empty space; the
    // double-click there must toggle once, not maximize and restore again.
    [AvaloniaFact]
    public void A_double_click_on_the_title_bars_empty_space_maximizes_once()
    {
        MainWindow window = WindowTestSupport.NewShownWindow();
        var titleBar = window.FindControl<Grid>("TitleBar")!;
        Point at = EmptyTitleBarSpot(window, titleBar);
        var presses = RecordPresses(window);

        DoubleClick(window, at);

        AssertState(WindowState.Maximized, window, at, presses);
    }

    /// <summary>
    /// A point whose press bubbles through <paramref name="titleBar"/> without reaching a button: a gap of the title bar.
    /// The title bar's margin is measured off the caption buttons after the first layout, so the spot is taken once the
    /// layout has settled, and kept only if hovering it changes nothing.
    /// </summary>
    private static Point EmptyTitleBarSpot(MainWindow window, Grid titleBar)
    {
        bool IsGap(Point p)
        {
            var chain = ((window.InputHitTest(p) as Visual)?.GetSelfAndVisualAncestors() ?? []).ToList();
            return chain.Contains(titleBar) && !chain.Any(v => v is Button or TabItem or TerminalPane);
        }

        for (int attempt = 0; attempt < 3; attempt++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Point origin = titleBar.TranslatePoint(default, window)!.Value;
            double bottom = Math.Min(origin.Y + titleBar.Bounds.Height, MainWindow.WindowDragBandHeight(false));
            for (double y = origin.Y + 1; y < bottom; y += 2)
            {
                for (double x = origin.X + 1; x < origin.X + titleBar.Bounds.Width; x += 2)
                {
                    var p = new Point(x, y);
                    if (!IsGap(p)) continue;
                    window.MouseMove(p);
                    Dispatcher.UIThread.RunJobs();
                    window.UpdateLayout();
                    if (IsGap(p)) return p;
                    goto rescan;
                }
            }

            break;
        rescan:;
        }

        Assert.Fail("Test setup failed: no spot of the title bar is free of its buttons.");
        return default;
    }
}
