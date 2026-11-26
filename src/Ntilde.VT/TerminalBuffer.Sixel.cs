namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private TerminalImage? _placingSixel;
    private long _lastSixelSourceEviction;
    private bool _retrySixelSourcePrune;
    public SixelSourceBudget SixelSources { get; init; } = new();

    /// <summary>Bounds the encoded Kitty and iTerm2 bytes kept for snapshots (#838).</summary>
    public ImageSourceBudget ImageSources { get; init; } = new();

    /// <summary>Applies SIXEL geometry even on headless hosts. Never writes placeholder text or wraps an image horizontally.</summary>
    internal bool PlaceSixelImage(object? handle, int widthCells, int heightCells, SixelSource? source = null,
        SixelPlacementProvenance? provenance = null, SixelRasterIntent? nativeRaster = null)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            handle = PrepareSixelPlacementHandle(handle, source);
            bool display = Modes.IsSixelDisplayMode;
            SnapCursorToLineSizeNoLock();
            int row = display ? 0 : _cursorRow;
            int col = display ? 0 : _cursorCol;
            int screenOrigin = _isAltScreen ? 0 : _scrollback.Count;
            TerminalImage? image = handle is null ? null : new TerminalImage(handle, col, screenOrigin + row, widthCells, heightCells)
            {
                IsSixelImage = true,
                SixelPlacement = provenance,
                NativeRaster = nativeRaster,
                SixelSource = source,
                // In the row's own columns: a double-size row holds half as many, each two cells wide.
                ClipRightColumns = Math.Max(0, col + widthCells - ColumnsForLineSize(_viewport[row].LineSize, Cols)),
                IsAltScreenImage = _isAltScreen,
            };
            if (!TryAddSixelPlacement(image)) return false;
            if (!display)
            {
                PlaceScrollingSixelImage(image, row, heightCells);
            }
            else if (image is not null)
            {
                image.ClipBottomRows = Math.Max(0, heightCells - Rows);
                ClipNativeSixel(image, screenOrigin, screenOrigin + Rows, Cols);
                RetireCoveredSixelImages(image);
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
        return true;
    }

    /// <summary>
    /// Prunes expired pending sources and retires an unusable native owner without publishing a placement.
    /// </summary>
    private object? PrepareSixelPlacementHandle(object? handle, SixelSource? source)
    {
        if (source is null) return handle;
        PruneExpiredSixelSources();
        if (source.Data is not null || !CanDiscardSixelHandle(handle)) return handle;
        if (handle is INativeSixelImage) QueueImageHandleRetirement(handle, disposeImmediately: true);
        return null;
    }

    /// <summary>
    /// Admits the handle before publishing it, then commits source retention with the alias already visible.
    /// </summary>
    private bool TryAddSixelPlacement(TerminalImage? image)
    {
        if (image is null) return true;
        if (!TryAdmitImageHandle(image.ImageHandle))
        {
            image.SixelSource?.Dispose();
            return false;
        }
        // Publish ownership before budget eviction can prune another placement
        // sharing the same opaque decoded handle.
        _images.Add(image);
        if (image.SixelSource is { } source)
        {
            SixelSources.Commit(source);
            PruneExpiredSixelSources();
        }
        return true;
    }

    /// <summary>
    /// Queues only this buffer's unclaimed native owner after placement refusal; never retires a borrowed allocation.
    /// </summary>
    internal void RetireUnplacedNativeSixel(INativeSixelImage image)
    {
        if (ReferenceEquals(image.Allocation.Owner, NativeSixelBudget) && image.Allocation.Owner.IsUnclaimed(image.Allocation))
            QueueImageHandleRetirement(image, disposeImmediately: true);
    }

    private static bool CanDiscardSixelHandle(object? handle) => CanDiscardSixelHandle(handle, out _);

    /// <summary>
    /// Reads native decode state once without its decode lock; in-flight and materialized owners must survive pruning.
    /// </summary>
    private static bool CanDiscardSixelHandle(object? handle, out bool decoding)
    {
        decoding = false;
        if (handle is INativeSixelImage native)
        {
            NativeSixelDecodeState state = native.DecodeState;
            decoding = state == NativeSixelDecodeState.Decoding;
            return state is NativeSixelDecodeState.Pending or NativeSixelDecodeState.Failed or NativeSixelDecodeState.Disposed;
        }
        return handle is SixelSource || handle is IDeferredSixelImage { IsMaterialized: false };
    }

    /// <summary>Failed skipped owners must progress even when their outstanding charge refuses the next reservation.</summary>
    internal void PruneBeforeNativeSixelAdmission()
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        bool needsDisposalFrame;
        try
        {
            PruneExpiredSixelSources();
            needsDisposalFrame = !_retiredImageHandles.IsEmpty;
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        // A prior requested frame may have been blocked by a snapshot. Retry the real
        // outside-lock consumer on later attempts; invalidation never returns credit.
        if (needsDisposalFrame) Invalidate();
    }

    /// <summary>
    /// Retires expired discardable placements and remembers in-flight owners for a later prune without new eviction.
    /// </summary>
    private void PruneExpiredSixelSources()
    {
        long version = SixelSources.EvictionVersion;
        if (version == _lastSixelSourceEviction && !_retrySixelSourcePrune) return;
        _lastSixelSourceEviction = version;
        _retrySixelSourcePrune = false;
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            var image = _images[i];
            if (image.SixelSource is not { Data: null }) continue;
            bool discard = CanDiscardSixelHandle(image.ImageHandle, out bool decoding);
            _retrySixelSourcePrune |= decoding;
            if (!discard) continue;
            RetireImage(image, disposeImmediately: true);
            _images.RemoveAt(i);
        }
    }

    private void PlaceScrollingSixelImage(TerminalImage? image, int row, int heightCells)
    {
        bool withinMargins = row >= ScrollTop && row <= ScrollBottom;
        _placingSixel = image;
        try
        {
            // Preserve the existing inline-image cursor contract without touching text/SGR.
            WriteCharCore('\r');
            for (int i = 0; i < heightCells; i++) WriteCharCore('\n');
        }
        finally { _placingSixel = null; }

        if (image is null) return;
        ClipNativeSixel(image, int.MinValue, int.MaxValue, Cols);
        if (!_isAltScreen && ScrollTop == 0 && ScrollBottom == Rows - 1)
        {
            if (image.NativeRaster is null || !DiscardImageBeforeOrigin(image)) return;
        }
        else
        {
            int baseRow = _isAltScreen ? 0 : _scrollback.Count;
            bool clipToMargins = withinMargins || image.NativeRaster is null;
            if (!ClipSixelImage(image, baseRow + (clipToMargins ? ScrollTop : 0), baseRow + (clipToMargins ? ScrollBottom : Rows - 1)))
                return;
        }
        RetireImage(image);
        _images.Remove(image);
    }

    private void RetireCoveredSixelImages(TerminalImage current)
    {
        // Compared in single-width columns: an image on a double-size row is drawn twice as wide.
        (int left, int right) = ImagePageColumnsNoLock(current);
        int top = current.CellY + current.ClipTopRows;
        int bottom = current.CellY + current.CellHeight - current.ClipBottomRows;
        if (right <= left || bottom <= top) return;
        // The SIXEL draw path clears this rectangle before painting. Fully covered older
        // SIXELs cannot reappear; retire them through the existing snapshot-session gate.
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage old = _images[i];
            if (!old.IsSixelImage || ReferenceEquals(old, current) || old.IsAltScreenImage != current.IsAltScreenImage) continue;
            if (old.NativeRaster is not null && !NativePixelsCoveredByClear(old, left, right, top, bottom)) continue;
            (int oldLeft, int oldRight) = ImagePageColumnsNoLock(old);
            if (oldLeft >= left && oldRight <= right &&
                old.CellY + old.ClipTopRows >= top && old.CellY + old.CellHeight - old.ClipBottomRows <= bottom)
            {
                RetireImage(old, disposeImmediately: true);
                _images.RemoveAt(i);
            }
        }
    }

    private bool ClipSixelImage(TerminalImage image, int top, int bottom)
    {
        if (!image.IsSixelImage || ReferenceEquals(image, _placingSixel)) return false;
        image.ClipTopRows = Math.Max(image.ClipTopRows, Math.Max(0, top - image.CellY));
        image.ClipBottomRows = Math.Max(image.ClipBottomRows, Math.Max(0, image.CellY + image.CellHeight - bottom - 1));
        if (image.NativeRaster is null) return image.ClipTopRows + image.ClipBottomRows >= image.CellHeight;
        image.ClipTopRows = Math.Min(image.ClipTopRows, image.CellHeight);
        image.ClipBottomRows = Math.Min(image.ClipBottomRows, image.CellHeight);
        ClipNativeSixel(image, top, bottom + 1, int.MaxValue);
        return !HasNativeSixelPixels(image) && !HasSixelClearRegion(image);
    }
}
