using System;
using System.Text.Json.Serialization;

namespace Ntilde.AgentHost.Contracts;

/// <summary>Opt-in detached facts from the exact offscreen draw. Not ownership, native-pixel
/// fidelity, hidden-asset inventory or completed experiment evidence.</summary>
public sealed record CaptureImageDetails
{
    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; init; } = 1;
    [JsonPropertyName("complete")]
    public bool Complete { get; init; }
    [JsonPropertyName("totalPlacements")]
    public int TotalPlacements { get; init; }
    /// <summary>Draw-snapshot order, at most MaxCaptureImagePlacements. Empty and incomplete
    /// on overflow; an empty complete list describes only the active viewport.</summary>
    [JsonPropertyName("placements")]
    public required CaptureImagePlacementDetails[] Placements { get; init; }
}

public sealed record CaptureImagePlacementDetails
{
    [JsonPropertyName("index")]
    public int Index { get; init; }
    /// <summary>Current in-process placement identity; not stable across state import.</summary>
    [JsonPropertyName("placementId")]
    public Guid PlacementId { get; init; }
    [JsonPropertyName("col")]
    public int Col { get; init; }
    [JsonPropertyName("viewportRow")]
    public int ViewportRow { get; init; }
    [JsonPropertyName("widthCells")]
    public int WidthCells { get; init; }
    [JsonPropertyName("heightCells")]
    public int HeightCells { get; init; }
    [JsonPropertyName("clipTopRows")]
    public int ClipTopRows { get; init; }
    [JsonPropertyName("clipBottomRows")]
    public int ClipBottomRows { get; init; }
    [JsonPropertyName("clipRightColumns")]
    public int ClipRightColumns { get; init; }
    [JsonPropertyName("preserveAspectRatio")]
    public bool PreserveAspectRatio { get; init; }
    /// <summary>Canvas dimensions of the bitmap actually submitted in this draw, not painted
    /// bounds. Null together if no bitmap was submitted; no extra decode is performed.</summary>
    [JsonPropertyName("bitmapWidthPx"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BitmapWidthPx { get; init; }
    [JsonPropertyName("bitmapHeightPx"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public int? BitmapHeightPx { get; init; }
    /// <summary>Null for legacy/imported/generic placements: native geometry is unqualified.</summary>
    [JsonPropertyName("sixelPlacement"), JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public CaptureSixelPlacementDetails? SixelPlacement { get; init; }
}

/// <summary>Exact parse-time reservation inputs, independent of later parser/UI/capture metrics.
/// Image-decoder origin means reported admission dimensions, not necessarily materialized pixels.</summary>
public sealed record CaptureSixelPlacementDetails
{
    [JsonPropertyName("reservationWidthPx")]
    public int ReservationWidthPx { get; init; }
    [JsonPropertyName("reservationHeightPx")]
    public int ReservationHeightPx { get; init; }
    /// <summary>sixel-geometry or image-decoder.</summary>
    [JsonPropertyName("dimensionOrigin")]
    public required string DimensionOrigin { get; init; }
    /// <summary>Exact sanitized float32 denominators widened to double for lossless JSON numbers, including minimum/default.</summary>
    [JsonPropertyName("cellWidthPx")]
    public double CellWidthPx { get; init; }
    [JsonPropertyName("cellHeightPx")]
    public double CellHeightPx { get; init; }
    /// <summary>sixel-override, parser-metric or default-fallback, independently per axis.</summary>
    [JsonPropertyName("cellWidthOrigin")]
    public required string CellWidthOrigin { get; init; }
    [JsonPropertyName("cellHeightOrigin")]
    public required string CellHeightOrigin { get; init; }
    /// <summary>float32-ceiling-v1: ceil(float32(dimension / denominator)), not float64 division.
    /// This records reservation behavior; it does not promise native pixel fidelity.</summary>
    [JsonPropertyName("arithmetic")]
    public string Arithmetic { get; init; } = "float32-ceiling-v1";
}
