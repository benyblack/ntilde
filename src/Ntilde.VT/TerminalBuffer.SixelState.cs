namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// Preserves full native intent or explicit history-cap loss; unavailable retained source refuses the whole native export.
    /// </summary>
    private void ExportSixelState(TerminalStateSnapshot snapshot)
    {
        for (int order = 0; order < _images.Count; order++)
        {
            TerminalImage image = _images[order];
            if (!image.IsSixelImage || image.SixelSource is not { } source) continue;
            if (ExportSixelPlacement(snapshot, image, source, order) is { } state)
                (snapshot.Sixels ??= []).Add(state);
        }
    }

    private TerminalSixelImageState? ExportSixelPlacement(TerminalStateSnapshot snapshot, TerminalImage image, SixelSource source, int order)
    {
        int y = image.IsAltScreenImage ? image.CellY : image.CellY - snapshot.ScrollbackRowsDropped;
        int end = image.IsAltScreenImage ? snapshot.Rows : snapshot.ScrollbackRowCount + snapshot.Rows;
        int top = Math.Max(image.ClipTopRows, Math.Max(0, -y));
        int bottom = Math.Max(image.ClipBottomRows, Math.Max(0, y + image.CellHeight - end));
        var intent = ExportNativeSixelRaster(snapshot, image, y);
        if (intent is not null)
        {
            top = Math.Min(top, image.CellHeight);
            bottom = Math.Min(bottom, image.CellHeight);
        }
        if (!ShouldExportSixelPlacement(snapshot.Cols, image, top, bottom, intent)) return null;
        if (source.Data is not { } data)
        {
            if (intent is not null)
                throw new InvalidOperationException("Native SIXEL state export is unavailable because its retained source expired.");
            return null;
        }
        return new TerminalSixelImageState
        {
            Source = data,
            X = image.CellX,
            Y = y,
            Width = image.CellWidth,
            Height = image.CellHeight,
            Alt = image.IsAltScreenImage,
            ClipTop = top,
            ClipBottom = bottom,
            ClipRight = image.ClipRightColumns,
            Order = order,
            NativeRaster = intent,
            SourceOnly = intent is not null && image.ImageHandle is SixelSource,
        };
    }

    private static bool ShouldExportSixelPlacement(int cols, TerminalImage image, int top, int bottom, SixelRasterIntent? intent)
    {
        bool visibleReservation = top + (long)bottom < image.CellHeight && image.CellX < cols;
        if (intent is null) return visibleReservation;
        bool clearing = visibleReservation && image.ClipRightColumns < image.CellWidth;
        var crop = intent.Crop;
        return clearing || (crop.Right > crop.Left && crop.Bottom > crop.Top);
    }

    private SixelRasterIntent? ExportNativeSixelRaster(TerminalStateSnapshot snapshot, TerminalImage image, int y)
    {
        var intent = image.NativeRaster;
        // Only the explicit scrollback cap discards history at export time.
        if (intent is null || image.IsAltScreenImage || snapshot.ScrollbackRowsDropped <= 0) return intent;
        var crop = intent.Crop;
        double upper = Math.Min(crop.Bottom, Math.Max(crop.Top,
            Math.Clamp(-(double)y * GetNativeSixelCellMetrics().HeightPx, 0, intent.CanvasHeight)));
        return new(intent.Version, intent.CanvasWidth, intent.CanvasHeight,
            new(crop.Left, upper, crop.Right, crop.Bottom));
    }

    /// <summary>
    /// Checks required native semantics before retention truncation; legacy source admission retains its existing bounds.
    /// </summary>
    private static void ValidateSixelState(TerminalStateSnapshot snapshot, bool nativeAllowed = false)
    {
        if (nativeAllowed)
        {
            ValidateNativeSixelState(snapshot);
            return;
        }
        if (snapshot.NativeSixelCellMetrics is not null)
            throw StateTransferValidation.Reject("native SIXEL metrics", "requires its native session policy");
        if (snapshot.Sixels is not { } states) return;
        // Reject required semantics even on entries a source-budget truncation would discard.
        // Never silently reinterpret native state as a legacy whole-cell placement.
        if (states.Any(state => state?.NativeRaster is not null || state?.SourceOnly == true))
            throw StateTransferValidation.Reject("SIXEL native raster", "native intent requires a native session");
        for (int i = Math.Max(0, states.Count - SixelSourceBudget.DefaultMaxSources); i < states.Count; i++)
        {
            var state = StateTransferValidation.RequirePresent(states[i], "SIXEL image");
            string data = StateTransferValidation.RequirePresent(state.Source, "SIXEL source");
            if (data.Length <= SixelSourceBudget.DefaultMaxChars && !AnsiParser.IsSixel(data))
                throw StateTransferValidation.Reject("SIXEL source", "invalid DCS header");
        }
        foreach (var state in BoundedSixelStates(snapshot))
        {
            var (width, height) = SixelGeometry.Measure(state.Source);
            if (width <= 0 || height <= 0) throw StateTransferValidation.Reject("SIXEL source", "empty raster");
        }
    }

    private static List<TerminalSixelImageState> BoundedSixelStates(TerminalStateSnapshot snapshot)
    {
        var kept = new List<TerminalSixelImageState>();
        if (snapshot.Sixels is not { } states) return kept;
        int chars = 0;
        for (int i = states.Count - 1; i >= Math.Max(0, states.Count - SixelSourceBudget.DefaultMaxSources); i--)
        {
            var state = states[i];
            if (state.Source.Length > SixelSourceBudget.DefaultMaxChars - chars) continue;
            chars += state.Source.Length;
            kept.Add(state);
        }
        kept.Reverse();
        return kept;
    }

    private void ImportSixelState(TerminalStateSnapshot snapshot, Dictionary<TerminalImage, int> order, int expiredRows)
    {
        foreach (var state in BoundedSixelStates(snapshot))
        {
            int width = Math.Clamp(state.Width, 1, SixelGeometry.DefaultMaxPixelDimension);
            int height = Math.Clamp(state.Height, 1, SixelGeometry.DefaultMaxPixelDimension);
            int y = state.Alt ? state.Y : state.Y - expiredRows;
            int top = Math.Clamp(Math.Max(state.ClipTop, -y), 0, height);
            int bottom = Math.Clamp(state.ClipBottom, 0, height - top);
            int right = Math.Clamp(state.ClipRight, 0, width);
            if (top + bottom >= height || right >= width) continue;
            var source = SixelSources.Retain(state.Source);
            if (source.Data is null) continue;
            int lastRow = state.Alt ? Rows - 1 : _scrollback.Count + Rows - 1;
            var image = new TerminalImage(source, Math.Clamp(state.X, 0, Cols - 1), Math.Clamp(y, 1 - height, lastRow), width, height)
            {
                IsSixelImage = true,
                SixelSource = source,
                IsAltScreenImage = state.Alt,
                ClipTopRows = top,
                ClipBottomRows = bottom,
                ClipRightColumns = right,
            };
            _images.Add(image);
            if (state.Order is int z) order[image] = z;
        }
        PruneExpiredSixelSources();
    }

    internal void DecodeTransferredSixels(IImageDecoder? decoder)
    {
        if (decoder is null) return;
        Lock.EnterWriteLock();
        try
        {
            foreach (var image in _images)
            {
                if (image.SixelSource is not { } source) continue;
                try
                {
                    if (decoder.DecodeSixel(source, out _, out _) is { } handle)
                    {
                        RequireImageHandleAdmission(handle);
                        image.ImageHandle = handle;
                    }
                }
                catch (Exception ex) { TerminalLogger.Debug($"[SIXEL] Snapshot decode failed: {ex.Message}"); }
            }
        }
        finally { Lock.ExitWriteLock(); }
        Invalidate();
    }
}
