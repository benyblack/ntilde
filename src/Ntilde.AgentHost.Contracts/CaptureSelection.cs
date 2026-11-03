using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Inclusive, ordered zero-based viewport cells to highlight in an offscreen image only.</summary>
public sealed record CaptureSelection
{
    [JsonPropertyName("startRow")] public required int StartRow { get; init; }
    [JsonPropertyName("startCol")] public required int StartCol { get; init; }
    [JsonPropertyName("endRow")] public required int EndRow { get; init; }
    [JsonPropertyName("endCol")] public required int EndCol { get; init; }
}
