using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
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
    /// A window must never be asked to be larger than the screen it opens on; the minimums shrink
    /// with it, or the platform would refuse the smaller size.
    /// </summary>
    [AvaloniaFact]
    public void FitWindow_ClampsToTheWorkingArea_IncludingMinimums()
    {
        UiScale.Apply(2.0);
        try
        {
            var window = new Window { Width = 880, Height = 620, MinWidth = 780, MinHeight = 520 };

            UiScale.FitWindow(window, workingArea: new Size(1600, 1000));

            Assert.Equal(1600, window.Width, precision: 6);
            Assert.Equal(1000, window.Height, precision: 6);
            Assert.Equal(1560, window.MinWidth, precision: 6);
            Assert.Equal(1000, window.MinHeight, precision: 6);
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
