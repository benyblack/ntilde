namespace Ntilde.VT;

/// <summary>The branch that supplied dimensions actually used to reserve SIXEL cells.
/// An image decoder may report measured dimensions before materializing a bitmap.</summary>
public enum SixelReservationDimensionOrigin { SixelGeometry, ImageDecoderReported }

/// <summary>The source of one sanitized cell-pixel denominator at placement creation.</summary>
public enum SixelReservationMetricOrigin { SixelOverride, ParserMetric, DefaultFallback }

/// <summary>Immutable creation facts, not a claim of native-pixel fidelity. Dimensions are the
/// admission canvas, not declared raster bounds or painted extent. Reservation uses float32
/// division followed by ceiling. Deliberately absent from persistent/transfer state.</summary>
public readonly record struct SixelPlacementProvenance(
    int ReservationWidthPx,
    int ReservationHeightPx,
    SixelReservationDimensionOrigin DimensionOrigin,
    float CellWidthPx,
    float CellHeightPx,
    SixelReservationMetricOrigin CellWidthOrigin,
    SixelReservationMetricOrigin CellHeightOrigin)
{
    internal static SixelReservationMetricOrigin MetricOrigin(float selected, bool hasOverride)
    {
        if (!float.IsFinite(selected) || selected <= 0) return SixelReservationMetricOrigin.DefaultFallback;
        return hasOverride ? SixelReservationMetricOrigin.SixelOverride : SixelReservationMetricOrigin.ParserMetric;
    }
}
