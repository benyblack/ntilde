using Ntilde.Controls;

namespace Ntilde.Tests.Controls;

/// <summary>
/// The kitty mode-2048 in-band resize report carries the terminal's size in pixels. The pane used
/// to multiply the grid by the cell size in DIPs, which is only pixels at 100% on a 96-dpi monitor;
/// under monitor scaling, and now under the interface scale, a terminal-browser client was told
/// a fraction of the pixels the terminal actually occupies (Codex P1 on PR #466). The report now
/// applies the view's effective render scale - monitor scale times interface scale.
/// </summary>
public sealed class InBandResizePixelReportTests
{
    [Theory]
    [InlineData(80, 24, 9.5f, 20f, 1.0, 760, 480)]
    [InlineData(80, 24, 9.5f, 20f, 2.0, 1520, 960)]
    [InlineData(120, 40, 8.25f, 17f, 1.5, 1485, 1020)]
    public void PixelDimensions_ApplyTheEffectiveRenderScale(
        int cols, int rows, float cellWidthDips, float cellHeightDips, double scale, int expectedWidth, int expectedHeight)
    {
        var (width, height) = TerminalPane.InBandPixelDimensions(cols, rows, cellWidthDips, cellHeightDips, scale);

        Assert.Equal(expectedWidth, width);
        Assert.Equal(expectedHeight, height);
    }

    [Fact]
    public void PixelDimensions_TreatANonPositiveScaleAsUnscaled()
    {
        var (width, height) = TerminalPane.InBandPixelDimensions(80, 24, 10f, 20f, 0.0);

        Assert.Equal(800, width);
        Assert.Equal(480, height);
    }
}
