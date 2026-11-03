using Avalonia;
using Ntilde.AgentHost;
using Ntilde.Shell;

namespace Ntilde.AppTests.AgentHost;

public sealed class AgentPaneResizeGeometryTests
{
    private static readonly AgentPaneResizeLayout Layout = new(
        new Size(1000, 800), new Size(804, 400), new Size(10, 20),
        new Size(320, 240), Size.Infinity);

    [Fact]
    public void Requested_grid_uses_measured_chrome_and_the_center_of_the_actual_grid_bucket()
    {
        Assert.True(AgentPaneResizeGeometry.TryGetClientSize(Layout, 60, 10, out var target));
        Assert.Equal(new Size(805, 610), target);
        var viewport = new Size(target.Width - 196, target.Height - 400);
        Assert.True(TerminalView.TryComputeGrid(viewport, 10, 20, out int cols, out int rows));
        Assert.Equal((60, 10), (cols, rows));
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(257, 20)]
    [InlineData(80, 0)]
    [InlineData(80, 129)]
    public void Invalid_requested_grid_is_rejected_before_geometry_mutation(int cols, int rows)
        => Assert.False(AgentPaneResizeGeometry.TryGetClientSize(Layout, cols, rows, out _));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_cell_measurements_cannot_invent_a_client_size(double width)
        => Assert.False(AgentPaneResizeGeometry.TryGetClientSize(Layout with { CellSize = new Size(width, 20) }, 60, 10, out _));

    [Fact]
    public void Native_window_limits_are_checked_before_requesting_an_unreachable_grid()
    {
        Assert.False(AgentPaneResizeGeometry.TryGetClientSize(Layout with { MinimumClientSize = new Size(900, 700) }, 60, 10, out _));
        Assert.False(AgentPaneResizeGeometry.TryGetClientSize(Layout with { MaximumClientSize = new Size(800, 600) }, 60, 10, out _));
    }
}
