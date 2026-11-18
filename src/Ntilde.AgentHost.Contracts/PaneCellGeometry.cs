using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>UI-thread observations of the current view, independent of parser query replies. Logical units are Avalonia DIPs.</summary>
public sealed record PaneCellGeometry
{
    [JsonPropertyName("viewRows")] public int ViewRows { get; init; }
    [JsonPropertyName("viewCols")] public int ViewCols { get; init; }
    [JsonPropertyName("cellWidthDip")] public double CellWidthDip { get; init; }
    [JsonPropertyName("cellHeightDip")] public double CellHeightDip { get; init; }
    [JsonPropertyName("fontAscentDip")] public double FontAscentDip { get; init; }
    [JsonPropertyName("renderScaling")] public double RenderScaling { get; init; }
    [JsonPropertyName("backingScale")] public double BackingScale { get; init; }
}

/// <summary>Read-only App properties, independent of the parser's host snapshot.</summary>
public sealed record PaneFontGeometry
{
    [JsonPropertyName("fontSizeDip")] public double FontSizeDip { get; init; }
    [JsonPropertyName("windowWidthDip")] public double WindowWidthDip { get; init; }
    [JsonPropertyName("windowHeightDip")] public double WindowHeightDip { get; init; }
}
