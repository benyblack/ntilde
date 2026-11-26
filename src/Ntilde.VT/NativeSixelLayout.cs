namespace Ntilde.VT;

/// <summary>Pure projection for native-image culling; supplied from the exact presenter's snapped draw grid.</summary>
public sealed record NativeSixelViewProjection
{
    public double PixelsPerCellWidth { get; }
    public double PixelsPerCellHeight { get; }

    /// <summary>
    /// Requires an explicit finite source-pixel-per-snapped-cell projection; neither parser metrics nor QA facts are inferred.
    /// </summary>
    public NativeSixelViewProjection(double pixelsPerCellWidth, double pixelsPerCellHeight)
    {
        if (!double.IsFinite(pixelsPerCellWidth) || pixelsPerCellWidth <= 0 ||
            !double.IsFinite(SixelGeometry.DefaultMaxPixelDimension / pixelsPerCellWidth))
            throw new ArgumentOutOfRangeException(nameof(pixelsPerCellWidth));
        if (!double.IsFinite(pixelsPerCellHeight) || pixelsPerCellHeight <= 0 ||
            !double.IsFinite(SixelGeometry.DefaultMaxPixelDimension / pixelsPerCellHeight))
            throw new ArgumentOutOfRangeException(nameof(pixelsPerCellHeight));
        PixelsPerCellWidth = pixelsPerCellWidth;
        PixelsPerCellHeight = pixelsPerCellHeight;
    }

    /// <summary>Includes surviving pixels or clearing cells, independently of the integer reservation height.</summary>
    public bool Intersects(SixelRasterIntent intent, int x, int y, int width, int height,
        int clipTop, int clipBottom, int clipRight, int top, int rows, int cols, int columnScale = 1)
    {
        double end = (double)top + rows;
        bool clearing = width > clipRight && height > (long)clipTop + clipBottom &&
            x * (double)columnScale < cols && (x + (double)width - clipRight) * columnScale > 0 &&
            y + (double)height - clipBottom > top && y + (double)clipTop < end;
        var crop = intent.Crop;
        bool pixels = crop.Right > crop.Left && crop.Bottom > crop.Top &&
            x * (double)columnScale + crop.Left / PixelsPerCellWidth < cols && x * (double)columnScale + crop.Right / PixelsPerCellWidth > 0 &&
            y + crop.Top / PixelsPerCellHeight < end && y + crop.Bottom / PixelsPerCellHeight > top;
        return clearing || pixels;
    }
}

public partial class TerminalBuffer
{
    private SixelCellSize? _nativeSixelCellMetrics;

    /// <summary>Canonical ordered stream metrics, independent of any render/capture request.</summary>
    internal SixelCellSize? NativeSixelCellMetrics => System.Threading.Volatile.Read(ref _nativeSixelCellMetrics);

    /// <summary>
    /// Publishes one validated canonical pair under the native buffer write cut without recropping existing images.
    /// </summary>
    internal void SetNativeSixelCellMetrics(SixelCellSize? metrics)
    {
        if (NativeSixelBudget is null) throw new InvalidOperationException("Native metrics require an admitted native buffer.");
        RequireNativeSixelMetrics(metrics);
        bool taken = EnterWriteLockIfNeeded();
        try { System.Threading.Volatile.Write(ref _nativeSixelCellMetrics, metrics); }
        finally { ExitWriteLockIfNeeded(Lock, taken); }
    }

    /// <summary>
    /// Rejects absent, nonfinite or subpixel native denominators before reservation can create unbounded cell loops.
    /// </summary>
    internal static void RequireNativeSixelMetrics(SixelCellSize? metrics)
    {
        if (metrics is null || !float.IsFinite(metrics.WidthPx) || !float.IsFinite(metrics.HeightPx) ||
            metrics.WidthPx < 1 || metrics.HeightPx < 1)
            throw StateTransferValidation.Reject("native SIXEL metrics", "requires an explicit finite pair of at least one pixel per cell");
    }

    /// <summary>Only ordered domain loss intersects native crop; readers and presenter changes never call this.</summary>
    private void ClipNativeSixel(TerminalImage image, int top, int bottomExclusive, int rightExclusive)
    {
        if (image.NativeRaster is not { } native) return;
        image.NativeRaster = IntersectNativeRaster(native, ImagePageColumnsNoLock(image).Left, image.CellY,
            top, bottomExclusive, rightExclusive, NativeSixelCellMetrics!);
    }

    /// <summary>
    /// Returns a monotonic source-pixel intersection for declared domain loss, retaining fractional and empty bounds.
    /// </summary>
    private static SixelRasterIntent IntersectNativeRaster(SixelRasterIntent native, int x, int y,
        int top, int bottomExclusive, int rightExclusive, SixelCellSize metrics)
    {
        var previous = native.Crop;
        double left = previous.Left;
        double upper = Math.Min(previous.Bottom, Math.Max(previous.Top,
            Math.Clamp(((double)top - y) * metrics.HeightPx, 0, native.CanvasHeight)));
        double right = Math.Min(previous.Right,
            Math.Clamp(((double)rightExclusive - x) * metrics.WidthPx, 0, native.CanvasWidth));
        double bottom = Math.Min(previous.Bottom,
            Math.Clamp(((double)bottomExclusive - y) * metrics.HeightPx, 0, native.CanvasHeight));
        return new(native.Version, native.CanvasWidth, native.CanvasHeight,
            new(left, upper, Math.Max(left, right), Math.Max(upper, bottom)));
    }

    /// <summary>
    /// Applies actual shape loss to both native screen banks; presentation changes alone never enter this path.
    /// </summary>
    private void ClipNativeSixelsAfterResize()
    {
        if (NativeSixelBudget is null) return;
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            var image = _images[i];
            if (image.NativeRaster is null) continue;
            int end = image.IsAltScreenImage ? Rows : _scrollback.Count + Rows;
            image.ClipTopRows = Math.Min(image.CellHeight, Math.Max(image.ClipTopRows, Math.Max(0, -image.CellY)));
            image.ClipBottomRows = Math.Min(image.CellHeight, Math.Max(image.ClipBottomRows, Math.Max(0, image.CellY + image.CellHeight - end)));
            image.ClipRightColumns = Math.Min(image.CellWidth, Math.Max(image.ClipRightColumns, Math.Max(0, image.CellX + image.CellWidth - Cols)));
            ClipNativeSixel(image, 0, end, Cols);
            if (HasNativeSixelPixels(image) || HasSixelClearRegion(image)) continue;
            RetireImage(image);
            _images.RemoveAt(i);
        }
    }

    /// <summary>
    /// An empty cell-clearing envelope is independent of a surviving native raster overhang.
    /// </summary>
    private static bool HasSixelClearRegion(TerminalImage image) =>
        image.ClipTopRows + (long)image.ClipBottomRows < image.CellHeight && image.ClipRightColumns < image.CellWidth;

    /// <summary>
    /// Keeps native raster lifetime separate from the rounded reservation and its clearing clips.
    /// </summary>
    private static bool HasNativeSixelPixels(TerminalImage image) => image.NativeRaster is { Crop: var crop } &&
        crop.Right > crop.Left && crop.Bottom > crop.Top;

    /// <summary>
    /// Uses canonical raster extent for ordered stream operations while preserving the legacy integer overlap rule.
    /// </summary>
    private bool ImageOverlapsRows(TerminalImage image, int top, int bottomExclusive)
    {
        if (image.NativeRaster is not { } native)
            return image.CellY + (long)image.CellHeight > top && image.CellY < bottomExclusive;
        var size = NativeSixelCellMetrics!;
        var projection = new NativeSixelViewProjection(size.WidthPx, size.HeightPx);
        return projection.Intersects(native, image.CellX, image.CellY, image.CellWidth, image.CellHeight,
            image.ClipTopRows, image.ClipBottomRows, image.ClipRightColumns, top, bottomExclusive - top, Cols,
            ImageLineSizeNoLock(image) == LineSize.SingleWidth ? 1 : 2);
    }

    /// <summary>
    /// Crops actual history eviction before deciding retirement; a native overhang can survive its entire cell reservation.
    /// </summary>
    private bool DiscardImageBeforeOrigin(TerminalImage image)
    {
        if (image.NativeRaster is null) return image.CellY + (long)image.CellHeight <= 0;
        return ClipSixelImage(image, 0, int.MaxValue - 1);
    }

    /// <summary>
    /// Proves canonical source-pixel containment; callers separately require containment of the old clearing envelope.
    /// </summary>
    private bool NativePixelsCoveredByClear(TerminalImage image, int left, int right, int top, int bottom)
    {
        if (image.NativeRaster is not { Crop: var crop }) return false;
        if (crop.Right <= crop.Left || crop.Bottom <= crop.Top) return true;
        var metrics = NativeSixelCellMetrics!;
        int anchorColumn = ImagePageColumnsNoLock(image).Left;
        return (double)anchorColumn * metrics.WidthPx + crop.Left >= (double)left * metrics.WidthPx &&
            (double)anchorColumn * metrics.WidthPx + crop.Right <= (double)right * metrics.WidthPx &&
            (double)image.CellY * metrics.HeightPx + crop.Top >= (double)top * metrics.HeightPx &&
            (double)image.CellY * metrics.HeightPx + crop.Bottom <= (double)bottom * metrics.HeightPx;
    }
}
