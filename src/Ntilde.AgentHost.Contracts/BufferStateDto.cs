using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Detached buffer facts for deterministic editing observations; no rendered or UI state.</summary>
public sealed record BufferStateDto
{
    /// <summary>Inclusive zero-based top scrolling margin.</summary>
    [JsonPropertyName("scrollTop")]
    public required int ScrollTop { get; init; }

    /// <summary>Inclusive zero-based bottom scrolling margin.</summary>
    [JsonPropertyName("scrollBottom")]
    public required int ScrollBottom { get; init; }

    [JsonPropertyName("originMode")]
    public required bool OriginMode { get; init; }

    [JsonPropertyName("autoWrap")]
    public required bool AutoWrap { get; init; }

    [JsonPropertyName("insertMode")]
    public required bool InsertMode { get; init; }

    [JsonPropertyName("pendingWrap")]
    public required bool PendingWrap { get; init; }

    [JsonPropertyName("alternateScreen")]
    public required bool AlternateScreen { get; init; }

    /// <summary>
    /// Exact retained history row count for a local pane, even while its alternate screen is active.
    /// Explicit null for windowless reads: their viewport import does not retain complete history.
    /// Use readScrollback for the bounded remote history projection.
    /// </summary>
    [JsonPropertyName("scrollbackLines"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public required int? ScrollbackLines { get; init; }
}
