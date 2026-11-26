using System;

namespace Ntilde.Shell;

/// <summary>
/// A detached production value describing the presenter's device pixels per view DIP (P).
/// It is independent of an offscreen capture's requested scale (S); neither is inferred from cells.
/// </summary>
internal sealed record NativeRasterPresenterContext
{
    internal double DevicePixelsPerDip { get; }

    /// <summary>
    /// Requires a finite presenter density that can express every admitted raster in Skia coordinates.
    /// </summary>
    internal NativeRasterPresenterContext(double devicePixelsPerDip)
    {
        if (!double.IsFinite(devicePixelsPerDip) || devicePixelsPerDip <= 0 ||
            !float.IsFinite((float)devicePixelsPerDip) ||
            devicePixelsPerDip < Ntilde.VT.SixelGeometry.DefaultMaxPixelDimension / (double)float.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(devicePixelsPerDip));
        DevicePixelsPerDip = devicePixelsPerDip;
    }
}
