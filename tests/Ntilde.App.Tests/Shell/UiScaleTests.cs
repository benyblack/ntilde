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
