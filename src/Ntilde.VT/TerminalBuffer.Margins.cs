namespace Ntilde.VT;

public partial class TerminalBuffer
{
    /// <summary>
    /// The zero-based left margin. It is the first column unless DECLRMM (?69) is set and DECSLRM narrowed the margins; resetting
    /// DECLRMM, RIS, DECSTR, DECALN and a resize restore the full width. Margins are page-wide state shared by both screens, like
    /// <see cref="ScrollTop"/>.
    /// </summary>
    public int LeftMargin { get; private set; }

    /// <summary>The zero-based right margin; the last column unless DECSLRM narrowed it (see <see cref="LeftMargin"/>).</summary>
    public int RightMargin { get; private set; }

    /// <summary>Whether DECSLRM narrowed the horizontal margins from the page edges.</summary>
    public bool HasHorizontalMargins => LeftMargin > 0 || RightMargin < Cols - 1;

    /// <summary>Whether the cursor column is between the horizontal margins (always, without margins).</summary>
    private bool CursorInHorizontalMarginsNoLock => _cursorCol >= LeftMargin && _cursorCol <= RightMargin;

    /// <summary>The rightmost column a cursor-relative operation may reach: the right margin, or the last column once the cursor is right of it.</summary>
    private int RightEdgeForCursorNoLock => _cursorCol <= RightMargin ? RightMargin : Cols - 1;

    /// <summary>The leftmost column a cursor-relative operation may reach: the left margin, or the first column once the cursor is left of it.</summary>
    private int LeftEdgeForCursorNoLock => _cursorCol >= LeftMargin ? LeftMargin : 0;

    /// <summary>Where CR, NEL, CNL and CPL return: the left margin in origin mode or from a column at or right of it, else the first column.</summary>
    private int CarriageReturnColumnNoLock => Modes.IsOriginMode || _cursorCol >= LeftMargin ? LeftMargin : 0;

    /// <summary>The blank a scroll brings in: a space in the theme's default colors, as a new full-width row has.</summary>
    private TerminalCell ThemeBlankCell() => new(' ', Theme.Foreground, Theme.Background, false, false, true, true);

    private void ResetHorizontalMarginsNoLock()
    {
        LeftMargin = 0;
        RightMargin = Math.Max(0, Cols - 1);
    }

    /// <summary>DECLRMM (?69): setting keeps the current (full-width) margins; resetting restores the full width.</summary>
    internal void SetLeftRightMarginMode(bool enable)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            Modes.IsLeftRightMarginMode = enable;
            if (!enable) ResetHorizontalMarginsNoLock();
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// DECSLRM: sets the zero-based inclusive horizontal margins while DECLRMM is set, then homes the cursor (to the top-left margin
    /// corner in origin mode). Margins are clamped to the page; a left margin that is not left of the right one is ignored, as is
    /// the whole request while DECLRMM is reset. Returns whether the margins were applied.
    /// </summary>
    internal bool SetLeftRightMargins(int left, int right)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (!Modes.IsLeftRightMarginMode || Cols < 2) return false;
            left = Math.Clamp(left, 0, Cols - 1);
            right = Math.Clamp(right, 0, Cols - 1);
            if (left >= right) return false;
            LeftMargin = left;
            RightMargin = right;
            _cursorRow = Modes.IsOriginMode ? ScrollTop : 0;
            _cursorCol = Modes.IsOriginMode ? LeftMargin : 0;
            _isPendingWrap = false;
            return true;
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>CUF and HPR: right by <paramref name="count"/>, stopping at the right margin, or at the last column once right of it.</summary>
    public void CursorForward(int count)
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            _cursorCol = (int)Math.Min(RightEdgeForCursorNoLock, (long)_cursorCol + Math.Max(0, count));
            _isPendingWrap = false;
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>CR semantics for CNL, CPL and NEL: <see cref="CarriageReturnColumnNoLock"/>.</summary>
    public void CarriageReturn()
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            _cursorCol = CarriageReturnColumnNoLock;
            _isPendingWrap = false;
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
    }

    /// <summary>
    /// IND (and NEL after its CR): at the bottom margin the scrolling region scrolls up, unless horizontal margins are set and the
    /// cursor column is outside them, in which case nothing moves; elsewhere the cursor moves down a row (within the page).
    /// </summary>
    public void Index()
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            // As before margins existed: a scroll leaves the pending wrap alone, a move clears it.
            if (_cursorRow == ScrollBottom)
            {
                if (CursorInHorizontalMarginsNoLock) ScrollUpInternal();
            }
            else
            {
                _cursorRow = Math.Min(_cursorRow + 1, Rows - 1);
                _isPendingWrap = false;
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>RI: the mirror of <see cref="Index"/> at the top margin.</summary>
    public void ReverseIndex()
    {
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            if (_cursorRow == ScrollTop)
            {
                if (CursorInHorizontalMarginsNoLock) ScrollDownInternal();
            }
            else
            {
                _cursorRow = Math.Max(_cursorRow - 1, 0);
                _isPendingWrap = false;
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    /// <summary>
    /// Moves the column band <paramref name="left"/>..<paramref name="right"/> of rows <paramref name="top"/>..<paramref name="bottom"/>
    /// up (<paramref name="up"/>) or down by <paramref name="count"/> rows; the vacated band rows take <paramref name="blank"/>. Cells keep
    /// their character, attributes, colors and side tables (extended text, hyperlink, underline decoration, protection). Wide glyphs
    /// split by a band edge are retired whole first, sized-text blocks touching the band are retired, and sticky images overlapping it
    /// are removed (they cannot move with part of their rows). Columns outside the band, row wrap flags and scrollback are untouched.
    /// </summary>
    private void ScrollBandNoLock(int top, int bottom, int left, int right, int count, bool up, TerminalCell blank)
    {
        if (top > bottom || left > right || count < 1) return;
        int n = Math.Min(count, bottom - top + 1);
        int width = right - left + 1;
        EraseTextSizingRange(left, right + 1, TextSizingAbsoluteRow(top), TextSizingAbsoluteRow(bottom) + 1, blank);
        RetireImagesInBandNoLock(top, bottom, left, right);

        int height = bottom - top + 1;
        var cells = new TerminalCell[height][];
        var text = new string?[height][];
        var links = new Links.Hyperlink?[height][];
        var underlines = new CellUnderline?[height][];
        var guarded = new bool[height][];
        for (int y = 0; y < height; y++)
        {
            var row = _viewport[top + y];
            // A glyph cut by a band edge is retired whole: neither half may stay behind or travel alone.
            RetireWideAcross(row, left, blank);
            RetireWideAcross(row, right + 1, blank);
            cells[y] = new TerminalCell[width];
            text[y] = new string?[width];
            links[y] = new Links.Hyperlink?[width];
            underlines[y] = new CellUnderline?[width];
            guarded[y] = new bool[width];
            for (int x = 0; x < width; x++)
            {
                int col = left + x;
                cells[y][x] = row.Cells[col];
                text[y][x] = row.GetExtendedText(col);
                links[y][x] = row.GetHyperlink(col);
                underlines[y][x] = row.GetUnderline(col);
                guarded[y][x] = row.IsProtected(col);
            }
        }

        for (int y = 0; y < height; y++)
        {
            int source = up ? y + n : y - n;
            var row = _viewport[top + y];
            row.NoteColumnsWritten(right + 1);
            for (int x = 0; x < width; x++)
            {
                int col = left + x;
                bool moved = source >= 0 && source < height;
                var cell = moved ? cells[source][x] : blank;
                cell.Flags |= (ushort)TerminalCellFlags.Dirty;
                row.Cells[col] = cell;
                row.SetExtendedText(col, moved ? text[source][x] : null);
                row.SetHyperlink(col, moved ? links[source][x] : null);
                row.SetUnderline(col, moved ? underlines[source][x] : null);
                row.SetProtected(col, moved && guarded[source][x]);
            }
            row.TouchRevision();
        }
    }

    /// <summary>Removes the active screen's sticky images that overlap the band; a partial-width move cannot carry them.</summary>
    private void RetireImagesInBandNoLock(int top, int bottom, int left, int right)
    {
        int absTop = _isAltScreen ? top : _scrollback.Count + top;
        int absBottom = _isAltScreen ? bottom : _scrollback.Count + bottom;
        for (int i = _images.Count - 1; i >= 0; i--)
        {
            var img = _images[i];
            if (img.IsAltScreenImage != _isAltScreen || !img.IsSticky) continue;
            bool rowsOverlap = img.CellY + img.CellHeight > absTop && img.CellY <= absBottom;
            bool colsOverlap = img.CellX + img.CellWidth > left && img.CellX <= right;
            if (!rowsOverlap || !colsOverlap) continue;
            RetireImage(img);
            _images.RemoveAt(i);
        }
    }
}
