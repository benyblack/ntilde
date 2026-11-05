using System.Text.Json.Serialization;

namespace Ntilde.VT;

/// <summary>Compatibility gate, not a parser option or an advertised capability.</summary>
public static class SixelRasterPolicy
{
    public const int PresenterNativeVersion = 1;
    // Activation requires native transforms, irreversible clipping, bounded ownership and peer negotiation.
    public const int EnabledVersion = 0;

    /// <summary>Admission is explicit; never clamp an unsupported requirement to legacy.</summary>
    public static void RequireSupported(int requiredVersion)
    {
        if (requiredVersion < 0 || requiredVersion > EnabledVersion)
            throw new InvalidOperationException("Unsupported SIXEL raster policy requirement.");
    }
}

/// <summary>Immutable source-pixel bounds; presentation changes must never expand these bounds.</summary>
public sealed record SixelSourceCrop
{
    [JsonPropertyName("left")] public double Left { get; }
    [JsonPropertyName("top")] public double Top { get; }
    [JsonPropertyName("right")] public double Right { get; }
    [JsonPropertyName("bottom")] public double Bottom { get; }

    [JsonConstructor]
    public SixelSourceCrop(double left, double top, double right, double bottom)
    {
        if (!double.IsFinite(left) || !double.IsFinite(top) || !double.IsFinite(right) || !double.IsFinite(bottom) ||
            left < 0 || top < 0 || right < left || bottom < top)
            throw new ArgumentOutOfRangeException(nameof(left), "Crop bounds must be finite, nonnegative and ordered.");
        Left = left;
        Top = top;
        Right = right;
        Bottom = bottom;
    }
}

/// <summary>
/// Versioned production intent, independent of decoder handles, font metrics and QA provenance.
/// This build can describe it but deliberately refuses to restore it while the policy is disabled.
/// </summary>
public sealed record SixelRasterIntent
{
    [JsonPropertyName("v")] public int Version { get; }
    [JsonPropertyName("w")] public int CanvasWidth { get; }
    [JsonPropertyName("h")] public int CanvasHeight { get; }
    [JsonPropertyName("crop")] public SixelSourceCrop Crop { get; }

    [JsonConstructor]
    public SixelRasterIntent(int version, int canvasWidth, int canvasHeight, SixelSourceCrop crop)
    {
        if (version != SixelRasterPolicy.PresenterNativeVersion)
            throw new ArgumentOutOfRangeException(nameof(version), "Unknown SIXEL raster intent version.");
        if (canvasWidth is < 1 or > SixelGeometry.DefaultMaxPixelDimension)
            throw new ArgumentOutOfRangeException(nameof(canvasWidth));
        if (canvasHeight is < 1 or > SixelGeometry.DefaultMaxPixelDimension)
            throw new ArgumentOutOfRangeException(nameof(canvasHeight));
        ArgumentNullException.ThrowIfNull(crop);
        if (crop.Right > canvasWidth || crop.Bottom > canvasHeight)
            throw new ArgumentOutOfRangeException(nameof(crop), "Crop exceeds its decoded canvas.");
        Version = version;
        CanvasWidth = canvasWidth;
        CanvasHeight = canvasHeight;
        Crop = crop;
    }
}
