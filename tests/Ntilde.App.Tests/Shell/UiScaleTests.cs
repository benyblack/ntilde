using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Ntilde.Shell;

namespace Ntilde.Tests.Shell;

/// <summary>
/// "Interface scale" is the app-wide zoom that the terminal font size never was: it grows tabs,
/// sidebars, dialogs and status text together, the way Warp's Zoom or VS Code's window zoom do.
/// It is applied once through the Window control theme, so any window the app opens - XAML or
/// code-built - picks it up without per-window wiring, and a change while windows are open
/// re-lays them out live.
/// </summary>
public sealed class UiScaleTests
{
    [Theory]
    [InlineData(1.25, 1.25)]
    [InlineData(0.1, UiScale.Minimum)]
    [InlineData(10.0, UiScale.Maximum)]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, UiScale.Maximum)]
    public void Clamp_KeepsTheScaleInsideTheSupportedRange(double requested, double expected)
    {
        Assert.Equal(expected, UiScale.Clamp(requested));
    }

    [Fact]
    public void Settings_DefaultToAnUnscaledInterface()
    {
        Assert.Equal(1.0, new TerminalSettings().UiScale);
    }

    /// <summary>
    /// Consumers that rasterize at device density (the terminal's glyph atlas) need to hear about
    /// a change; the value they get is the clamped one that was actually applied. AvaloniaFact,
    /// not Fact: Apply touches Application.Resources and must run on the UI thread - as a plain
    /// Fact this test ran on an xunit worker thread and poisoned the media context for 29 later
    /// window tests.
    /// </summary>
    [AvaloniaFact]
    public void Apply_RaisesChanged_WithTheClampedScale()
    {
        double? observed = null;
        EventHandler<double> handler = (_, scale) => observed = scale;
        UiScale.Changed += handler;
        try
        {
            UiScale.Apply(10.0);
            Assert.Equal(UiScale.Maximum, observed);
        }
        finally
        {
            UiScale.Changed -= handler;
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void Apply_ScalesTheContentOfAWindowOpenedAfterwards()
    {
        UiScale.Apply(1.5);
        try
        {
            var probe = new Border { Width = 100, Height = 80 };
            var window = new Window { Width = 400, Height = 300, Content = probe };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            try
            {
                Assert.Equal(1.5, UiScale.Current);
                AssertScaledSize(probe, window, expectedWidth: 150, expectedHeight: 120);
            }
            finally
            {
                window.Close();
            }
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void Apply_RescalesWindowsThatAreAlreadyOpen()
    {
        UiScale.Apply(1.0);
        var probe = new Border { Width = 100, Height = 80 };
        var window = new Window { Width = 400, Height = 300, Content = probe };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        try
        {
            AssertScaledSize(probe, window, expectedWidth: 100, expectedHeight: 80);

            UiScale.Apply(2.0);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            AssertScaledSize(probe, window, expectedWidth: 200, expectedHeight: 160);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A fixed-size window (dialogs, Settings) lays out in unscaled DIPs inside the transform, so
    /// at 200% an 880-DIP-wide window offers only 440 DIPs of room and clips its own content
    /// (Codex on PR #466: the Interface scale slider itself became unreachable). FitWindow grows
    /// the window's requested size and minimums by the current scale so the logical room stays
    /// what the XAML was designed for. NaN (SizeToContent) dimensions are left alone.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_GrowsRequestedSizeAndMinimumsByTheCurrentScale()
    {
        UiScale.Apply(1.5);
        try
        {
            var window = new Window { Width = 800, Height = 600, MinWidth = 700, MinHeight = 500 };

            UiScale.FitWindow(window, workingArea: null);

            Assert.Equal(1200, window.Width, precision: 6);
            Assert.Equal(900, window.Height, precision: 6);
            Assert.Equal(1050, window.MinWidth, precision: 6);
            Assert.Equal(750, window.MinHeight, precision: 6);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void FitWindow_LeavesSizeToContentDimensionsAlone()
    {
        UiScale.Apply(2.0);
        try
        {
            var window = new Window { Width = 460, SizeToContent = SizeToContent.Height };

            UiScale.FitWindow(window, workingArea: null);

            Assert.Equal(920, window.Width, precision: 6);
            Assert.True(double.IsNaN(window.Height));
            Assert.Equal(0, window.MinWidth);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A window must never be asked to be larger than the screen it opens on. Capping the outer
    /// size alone would bring the clipping back (Codex on PR #466: a 1920px display at 200% OS
    /// scaling is ~960 DIPs, so Settings at 200% would get 480 logical DIPs and lose the slider),
    /// so the scale itself is reduced to what fits and the window is pinned at that reduced
    /// scale: full logical room, smaller than the rest of the app, and the slider stays reachable.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_ReducesTheScaleAndPinsTheWindow_WhenTheScreenCannotFitIt()
    {
        UiScale.Apply(2.0);
        try
        {
            var window = new Window { Width = 880, Height = 620, MinWidth = 780, MinHeight = 520 };

            double applied = UiScale.FitWindow(window, workingArea: new Size(1000, 1000));

            double expected = 1000.0 / 880.0;
            Assert.Equal(expected, applied, precision: 4);
            Assert.Equal(1000, window.Width, precision: 3);
            Assert.Equal(620 * expected, window.Height, precision: 3);
            Assert.Equal(780 * expected, window.MinWidth, precision: 3);
            Assert.Equal(520 * expected, window.MinHeight, precision: 3);

            Assert.True(window.Resources.TryGetResource(UiScale.TransformResourceKey, null, out object? pinned));
            var transform = Assert.IsType<ScaleTransform>(pinned);
            Assert.Equal(expected, transform.ScaleX, precision: 4);
            Assert.Equal(expected, transform.ScaleY, precision: 4);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void FitWindow_ReturnsTheCurrentScale_AndLeavesTheWindowUnpinned_WhenItFits()
    {
        UiScale.Apply(1.5);
        try
        {
            var window = new Window { Width = 880, Height = 620 };

            double applied = UiScale.FitWindow(window, workingArea: new Size(2000, 1500));

            Assert.Equal(1.5, applied, precision: 6);
            Assert.Equal(1320, window.Width, precision: 3);
            Assert.False(window.Resources.ContainsKey(UiScale.TransformResourceKey), "a window that fits previews live and must not be pinned");
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A fixed-size window that is already open when the scale changes (Codex on PR #466: the
    /// Connection Manager sitting next to Settings while the slider moves) used to get its content
    /// rescaled but keep its frame, so at 200% it offered half the room it was designed for.
    /// FitWindow remembers the design size and Apply refits every open, unpinned window from it.
    /// </summary>
    [AvaloniaFact]
    public void Apply_RefitsOpenFixedWindows_FromTheirDesignSize()
    {
        UiScale.Apply(1.0);
        var window = new Window { Width = 800, Height = 600, MinWidth = 700, MinHeight = 500 };
        try
        {
            UiScale.FitWindow(window, workingArea: null);
            window.Show();
            Dispatcher.UIThread.RunJobs();

            UiScale.Apply(1.5);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1200, window.Width, precision: 3);
            Assert.Equal(900, window.Height, precision: 3);
            Assert.Equal(1050, window.MinWidth, precision: 3);
            Assert.Equal(750, window.MinHeight, precision: 3);

            UiScale.Apply(1.0);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(800, window.Width, precision: 3);
            Assert.Equal(600, window.Height, precision: 3);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    /// <summary>A pinned window (Settings, or one the screen forced smaller) holds its size as well as its scale.</summary>
    [AvaloniaFact]
    public void Apply_LeavesPinnedWindowsAlone()
    {
        UiScale.Apply(1.0);
        var window = new Window { Width = 800, Height = 600 };
        try
        {
            UiScale.PinScale(window, UiScale.FitWindow(window, workingArea: null));
            window.Show();
            Dispatcher.UIThread.RunJobs();

            UiScale.Apply(1.5);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(800, window.Width, precision: 3);
            Assert.Equal(600, window.Height, precision: 3);
        }
        finally
        {
            window.Close();
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// Fitting is idempotent from the remembered design size, so a second fit against the real
    /// screen (known only once the window opens, Codex on PR #466) does not compound the first.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_RefitsFromTheDesignSize_NotTheCurrentSize()
    {
        UiScale.Apply(2.0);
        try
        {
            var window = new Window { Width = 880, Height = 620, MinWidth = 780, MinHeight = 520 };

            UiScale.FitWindow(window, workingArea: null);
            Assert.Equal(1760, window.Width, precision: 3);

            double applied = UiScale.FitWindow(window, workingArea: new Size(1000, 1000));

            Assert.Equal(1000.0 / 880.0, applied, precision: 4);
            Assert.Equal(1000, window.Width, precision: 3);
            Assert.Equal(620 * applied, window.Height, precision: 3);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A screen-forced reduction is not a hold: when a later fit (the real screen once the window
    /// opens, or a lower app scale) has room again, the window grows back and its pin comes off,
    /// so it previews live like any other window.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_ReleasesAScreenReduction_WhenALaterFitHasRoom()
    {
        UiScale.Apply(2.0);
        try
        {
            var window = new Window { Width = 880, Height = 620 };
            UiScale.FitWindow(window, workingArea: new Size(1000, 1000));
            Assert.True(window.Resources.ContainsKey(UiScale.TransformResourceKey));

            double applied = UiScale.FitWindow(window, workingArea: new Size(3000, 3000));

            Assert.Equal(2.0, applied, precision: 6);
            Assert.Equal(1760, window.Width, precision: 3);
            Assert.False(window.Resources.ContainsKey(UiScale.TransformResourceKey), "the reduction pin must come off once the window fits");
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>
    /// A caller's hold (Settings pins the app scale it opened with) is the ceiling for every later
    /// fit: the screen may still reduce below it, and a fit with room comes back up to the held
    /// scale, never to a Current that moved on in the meantime.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_UnderAHold_UsesTheHeldScaleAsTheCeiling()
    {
        UiScale.Apply(1.5);
        try
        {
            var window = new Window { Width = 880, Height = 620 };
            UiScale.PinScale(window, 1.5);

            UiScale.Apply(2.0);
            double applied = UiScale.FitWindow(window, workingArea: new Size(3000, 3000));

            Assert.Equal(1.5, applied, precision: 6);
            Assert.Equal(1320, window.Width, precision: 3);
            var transform = Assert.IsType<ScaleTransform>(window.Resources[UiScale.TransformResourceKey]);
            Assert.Equal(1.5, transform.ScaleX, precision: 6);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    /// <summary>The reduction never goes below 100%: a window too big for the screen at 100% is not this feature's problem.</summary>
    [AvaloniaFact]
    public void FitWindow_NeverReducesBelowOneHundredPercent()
    {
        UiScale.Apply(1.5);
        try
        {
            var window = new Window { Width = 880, Height = 620 };

            double applied = UiScale.FitWindow(window, workingArea: new Size(600, 400));

            Assert.Equal(1.0, applied, precision: 6);
            Assert.Equal(880, window.Width, precision: 3);
        }
        finally
        {
            UiScale.Apply(1.0);
        }
    }

    [AvaloniaFact]
    public void FitWindow_IsANoOpAtOneHundredPercent()
    {
        UiScale.Apply(1.0);
        var window = new Window { Width = 800, Height = 600, MinWidth = 700, MinHeight = 500 };

        UiScale.FitWindow(window, workingArea: null);

        Assert.Equal(800, window.Width);
        Assert.Equal(600, window.Height);
        Assert.Equal(700, window.MinWidth);
        Assert.Equal(500, window.MinHeight);
    }

    /// <summary>
    /// The probe's own Bounds stay 100x80 (layout runs in unscaled units); what changes is how big
    /// it is in window coordinates, which is what the user sees. TranslatePoint composes the layout
    /// transform's render scale, so the far corner lands at origin + (w*scale, h*scale).
    /// </summary>
    private static void AssertScaledSize(Control probe, Window window, double expectedWidth, double expectedHeight)
    {
        Point? origin = probe.TranslatePoint(new Point(0, 0), window);
        Point? corner = probe.TranslatePoint(new Point(probe.Bounds.Width, probe.Bounds.Height), window);
        Assert.NotNull(origin);
        Assert.NotNull(corner);
        Assert.Equal(expectedWidth, corner!.Value.X - origin!.Value.X, precision: 1);
        Assert.Equal(expectedHeight, corner.Value.Y - origin.Value.Y, precision: 1);
    }
}
