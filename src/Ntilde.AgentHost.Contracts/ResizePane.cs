using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Explicitly requested grid for a visible local pane; requires the separate act permission.</summary>
public sealed record ResizePaneParams
{
    [JsonPropertyName("paneId")]
    public required Guid PaneId { get; init; }

    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    [JsonPropertyName("cols")]
    public required int Cols { get; init; }
}

/// <summary>Measured App geometry after a bounded resize attempt, including partial results.</summary>
public sealed record ResizePaneResult
{
    /// <summary>Actual VT-buffer height at observation time, not the requested height.</summary>
    [JsonPropertyName("rows")]
    public required int Rows { get; init; }

    /// <summary>Actual VT-buffer width at observation time, not the requested width.</summary>
    [JsonPropertyName("cols")]
    public required int Cols { get; init; }

    /// <summary>The view and buffer agree after the session's resize callback completed.</summary>
    [JsonPropertyName("layoutSettled")]
    public required bool LayoutSettled { get; init; }

    /// <summary>The settled actual App grid matches the request. This does not attest the child's kernel geometry.</summary>
    [JsonPropertyName("exact")]
    public required bool Exact { get; init; }
}
