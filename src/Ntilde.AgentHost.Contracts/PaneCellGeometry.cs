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
}
