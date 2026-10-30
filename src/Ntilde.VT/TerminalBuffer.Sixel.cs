namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private TerminalImage? _placingSixel;
    public SixelSourceBudget SixelSources { get; init; } = new();

    /// <summary>Applies SIXEL geometry even on headless hosts. Never writes placeholder text or wraps an image horizontally.</summary>
    internal void PlaceSixelImage(object? handle, int widthCells, int heightCells, SixelSource? source = null)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            bool display = Modes.IsSixelDisplayMode;
            int row = display ? 0 : _cursorRow;
            int col = display ? 0 : _cursorCol;
            int absoluteRow = _isAltScreen ? row : _scrollback.Count + row;
            TerminalImage? image = handle is null ? null : new TerminalImage(handle, col, absoluteRow, widthCells, heightCells)
            {
                IsSixelImage = true,
                SixelSource = source,
                ClipRightColumns = Math.Max(0, col + widthCells - Cols),
                IsAltScreenImage = _isAltScreen,
            };
            if (image is not null) _images.Add(image);
            if (display)
            {
                if (image is not null) image.ClipBottomRows = Math.Max(0, heightCells - Rows);
            }
            else PlaceScrollingSixelImage(image, row, heightCells);
            if (display && image is not null) RetireCoveredSixelImages(image);
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
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

        if (image is not null && (_isAltScreen || ScrollTop != 0 || ScrollBottom != Rows - 1))
        {
            int baseRow = _isAltScreen ? 0 : _scrollback.Count;
            if (ClipSixelImage(image, baseRow + (withinMargins ? ScrollTop : 0), baseRow + (withinMargins ? ScrollBottom : Rows - 1)))
            {
                RetireImage(image);
                _images.Remove(image);
            }
        }
    }

    private void RetireCoveredSixelImages(TerminalImage current)
    {
        int right = current.CellX + current.CellWidth - current.ClipRightColumns;
        int top = current.CellY + current.ClipTopRows;
        int bottom = current.CellY + current.CellHeight - current.ClipBottomRows;
        if (right <= current.CellX || bottom <= top) return;
        // The SIXEL draw path clears this rectangle before painting. Fully covered older
        // SIXELs cannot reappear; retire them through the existing snapshot-session gate.
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            TerminalImage old = _images[i];
            if (!old.IsSixelImage || ReferenceEquals(old, current) || old.IsAltScreenImage != current.IsAltScreenImage) continue;
            if (old.CellX >= current.CellX && old.CellX + old.CellWidth - old.ClipRightColumns <= right &&
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
        return image.ClipTopRows + image.ClipBottomRows >= image.CellHeight;
    }
}
