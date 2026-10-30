namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private TerminalImage? _placingSixel;

    /// <summary>Applies SIXEL geometry even on headless hosts. Never writes placeholder text or wraps an image horizontally.</summary>
    internal void PlaceSixelImage(object? handle, int widthCells, int heightCells)
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
                ClipRightColumns = Math.Max(0, col + widthCells - Cols),
                IsAltScreenImage = _isAltScreen,
            };
            if (image is not null) _images.Add(image);
            if (display)
            {
                if (image is not null) image.ClipBottomRows = Math.Max(0, heightCells - Rows);
            }
            else PlaceScrollingSixelImage(image, row, heightCells);
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

    private bool ClipSixelImage(TerminalImage image, int top, int bottom)
    {
        if (!image.IsSixelImage || ReferenceEquals(image, _placingSixel)) return false;
        image.ClipTopRows = Math.Max(image.ClipTopRows, Math.Max(0, top - image.CellY));
        image.ClipBottomRows = Math.Max(image.ClipBottomRows, Math.Max(0, image.CellY + image.CellHeight - bottom - 1));
        return image.ClipTopRows + image.ClipBottomRows >= image.CellHeight;
    }
}
