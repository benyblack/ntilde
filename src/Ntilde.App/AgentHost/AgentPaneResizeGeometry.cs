using Avalonia;
using Ntilde.AgentHost.Contracts;

namespace Ntilde.AgentHost;

/// <summary>Detached measurements from one UI-thread layout; cell dimensions are in DIPs.</summary>
internal readonly record struct AgentPaneResizeLayout(
    Size ClientSize, Size ViewportSize, Size CellSize, Size MinimumClientSize, Size MaximumClientSize);

/// <summary>Translate a requested cell grid into a client-size change without assuming hidden chrome dimensions.</summary>
internal static class AgentPaneResizeGeometry
{
    internal static bool TryGetClientSize(AgentPaneResizeLayout layout, int cols, int rows, out Size target)
    {
        target = default;
        if (cols < 1 || cols > AgentHostProtocol.MaxResizeCols || rows < 1 || rows > AgentHostProtocol.MaxResizeRows ||
            !Positive(layout.ClientSize.Width) || !Positive(layout.ClientSize.Height) ||
            !Positive(layout.ViewportSize.Width) || !Positive(layout.ViewportSize.Height) ||
            !Positive(layout.CellSize.Width) || !Positive(layout.CellSize.Height))
        {
            return false;
        }

        // TerminalView uses floor((width - 4) / cellWidth) and floor(height / cellHeight).
        // Aim at each bucket's center rather than a floating-point or native-pixel boundary.
        double width = layout.ClientSize.Width + (cols + 0.5) * layout.CellSize.Width + 4 - layout.ViewportSize.Width;
        double height = layout.ClientSize.Height + (rows + 0.5) * layout.CellSize.Height - layout.ViewportSize.Height;
        if (!Positive(width) || !Positive(height) ||
            !Fits(width, layout.MinimumClientSize.Width, layout.MaximumClientSize.Width) ||
            !Fits(height, layout.MinimumClientSize.Height, layout.MaximumClientSize.Height))
        {
            return false;
        }
        target = new Size(width, height);
        return true;
    }

    private static bool Positive(double value) => double.IsFinite(value) && value > 0;

    private static bool Fits(double value, double minimum, double maximum)
        => double.IsFinite(minimum) && minimum >= 0 && !double.IsNaN(maximum) && maximum >= minimum
            && value >= minimum && value <= maximum;
}
