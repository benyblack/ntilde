using System.Globalization;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private const long MaxTextSizingStorage = 16L * 1024 * 1024;
    private HashSet<TextSizingBlock>? _textSizingBlocks;
    private long _textSizingStorage;
    private bool _textSizingPruneRequired = true;
    private (long Generation, long Evicted) _textSizingPrunedHistory;

    internal bool HasSizedTextOnScreen()
    {
        AssertLockHeld();
        if (_textSizingBlocks == null) return false;
        foreach (var row in _viewport)
            if (row.GetTextSizingMap() is { Count: > 0 }) return true;
        return false;
    }

    public CellTextSizing? GetTextSizingAbsolute(int col, int absRow)
    {
        AssertLockHeld();
        if (_textSizingBlocks == null || col < 0 || col >= Cols || absRow < 0) return null;
        if (!_isAltScreen && absRow < _scrollback.Count)
        {
            var map = _scrollback.GetTextSizingMap(absRow);
            return map != null && map.TryGet(col, out var piece) ? piece : null;
        }
        return GetRowAbsolute(absRow)?.GetTextSizing(col);
    }

    public void WriteSizedText(string text, TextSizingOptions options)
    {
        if (!options.IsValid || !TextSizingOptions.IsSafeText(text)) return;
        bool lockTaken = EnterWriteLockIfNeeded();
        try
        {
            _highSurrogateBuffer = null;
            if (options.Scale == 1 && options.Width == 0 && !options.IsFractional) WriteContentCore(text);
            else if (options.Width != 0) WriteSizedBlock(text, options, options.Width);
            else
            {
                var elements = StringInfo.GetTextElementEnumerator(text);
                while (elements.MoveNext())
                {
                    string grapheme = elements.GetTextElement();
                    int width = GetGraphemeWidth(grapheme);
                    if (width > 0) WriteSizedBlock(grapheme, options, width);
                    else if (!TryAppendToSizedBlock(grapheme)) WriteContentCore(grapheme);
                }
            }
        }
        finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        Invalidate();
    }

    private void PrepareTextSizingResize(int cols, int rows)
    {
        if (_textSizingBlocks == null) return;
        _textSizingPruneRequired = true;
        var remove = new Dictionary<TextSizingBlock, (int X, int Y)>();
        void Collect(Ntilde.VT.Storage.SmallMap<CellTextSizing>? map, int row)
        {
            map?.ForEach((col, piece) =>
            {
                int x = col - piece.ColumnOffset, y = row - piece.RowOffset;
                if (x + piece.Block.Width > cols || piece.Block.Height > rows || _isAltScreen && y + piece.Block.Height > rows)
                    remove.TryAdd(piece.Block, (x, y));
            });
        }
        if (!_isAltScreen)
            for (int r = 0; r < _scrollback.Count; r++) Collect(_scrollback.GetTextSizingMap(r), r);
        for (int r = 0; r < _viewport.Length; r++) Collect(_viewport[r].GetTextSizingMap(), TextSizingAbsoluteRow(r));
        foreach (var (block, position) in remove) EraseTextSizingBlock(block, position.X, position.Y);
        PrepareInactiveTextSizingResize(cols, rows);
    }

    private void PrepareInactiveTextSizingResize(int cols, int rows)
    {
        var inactive = _isAltScreen ? _mainScreen : _altScreen;
        var discarded = new HashSet<TextSizingBlock>();
        for (int r = 0; r < inactive.Length; r++)
            inactive[r].GetTextSizingMap()?.ForEach((c, piece) =>
            {
                int x = c - piece.ColumnOffset;
                if (x + piece.Block.Width > cols || piece.Block.Height > rows
                    || !_isAltScreen && r - piece.RowOffset + piece.Block.Height > rows)
                    discarded.Add(piece.Block);
            });
        ClearDiscardedSizingRows(inactive, discarded);
        foreach (var block in discarded)
            if (_textSizingBlocks?.Remove(block) == true) _textSizingStorage -= block.StorageCost;
    }

    private static void ClearDiscardedSizingRows(TerminalRow[] rows, HashSet<TextSizingBlock> discarded)
    {
        foreach (var row in rows)
            for (int c = 0; c < row.Cells.Length; c++)
                if (row.GetTextSizing(c) is { } piece && discarded.Contains(piece.Block))
                {
                    ClearSizedCell(row, c);
                    row.SetTextSizing(c, null);
                    row.TouchRevision();
                }
    }

    private static void ClearSizedCell(TerminalRow row, int col)
    {
        row.Cells[col] = TerminalCell.Default;
        row.SetExtendedText(col, null);
        row.SetHyperlink(col, null);
        row.SetUnderline(col, null);
    }

    private static CellTextSizing? ReadSizingMap(Storage.SmallMap<CellTextSizing>? map, int col)
        => map?.TryGet(col, out CellTextSizing? piece) == true ? piece : null;

    private int TextSizingAbsoluteRow(int viewportRow) => _isAltScreen ? viewportRow : _scrollback.Count + viewportRow;

    private void WriteSizedBlock(string text, TextSizingOptions options, int baseWidth)
    {
        int width = baseWidth * options.Scale;
        int height = options.Scale;
        if (width > Cols || height > ScrollBottom - ScrollTop + 1) return;
        var block = new TextSizingBlock(text, options, baseWidth);
        if (!ReserveTextSizing(block, _cursorCol, TextSizingAbsoluteRow(_cursorRow), IsTextSizingTargetStable(block))) return;
        FindTextSizingWritePosition(width);
        FitTextSizingHeight(height);
        int absTop = TextSizingAbsoluteRow(_cursorRow);
        EraseTextSizingRange(_cursorCol, _cursorCol + width, absTop, absTop + height);
        int top = _cursorRow;
        SyncPackedState();
        for (int dy = 0; dy < height; dy++)
        {
            _cursorRow = top + dy;
            StoreTextSizingRow(block, dy);
        }
        FinishTextSizingWrite(top, width, text);
    }

    private bool IsTextSizingTargetStable(TextSizingBlock block)
        => !_isPendingWrap && _cursorCol + block.Width <= Cols && LowerTextSizingEnd(_cursorCol, block.Width) < 0
            && _cursorRow + block.Height <= (_cursorRow <= ScrollBottom ? ScrollBottom + 1 : Rows);

    private void FitTextSizingHeight(int height)
    {
        if (_cursorRow <= ScrollBottom)
        {
            int extra = Math.Max(0, height - (ScrollBottom - _cursorRow + 1));
            for (int i = 0; i < extra; i++) ScrollUpInternal();
            _cursorRow -= extra;
        }
        else if (_cursorRow + height > Rows) _cursorRow = Rows - height;
    }

    private void StoreTextSizingRow(TextSizingBlock block, int dy)
    {
        if (Modes.IsInsertMode) InsertCharactersInternal(block.Width);
        var row = _viewport[_cursorRow];
        row.NoteColumnsWritten(_cursorCol + block.Width);
        for (int dx = 0; dx < block.Width; dx++)
        {
            int col = _cursorCol + dx;
            bool anchor = dx == 0 && dy == 0;
            ushort flags = _packedFlags;
            if (anchor && block.Text.Length > 1) flags |= (ushort)TerminalCellFlags.HasExtendedText;
            row.Cells[col] = new TerminalCell(anchor ? block.Text[0] : ' ', _packedFg, _packedBg, flags);
            row.SetExtendedText(col, anchor && block.Text.Length > 1 ? block.Text : null);
            row.SetHyperlink(col, _currentHyperlink);
            row.SetUnderline(col, UnderlineForWrite);
            row.SetTextSizing(col, new(block, dx, dy));
        }
        row.TouchRevision();
    }

    private void FinishTextSizingWrite(int top, int width, string text)
    {
        _cursorRow = top;
        _lastCharCol = _cursorCol;
        _lastCharRow = top;
        _cursorCol += width;
        _isPendingWrap = Modes.IsAutoWrapMode && _cursorCol >= Cols;
        _cursorCol = Math.Min(_cursorCol, Cols - 1);
        _maxColThisRow = Math.Max(_maxColThisRow, _cursorCol);
        _prevCursorCol = _cursorCol;
        _prevCursorRow = _cursorRow;
        _isAfterZwj = EndsWithZwj(text);
    }

    /// <summary>Lower rows of existing blocks are skipped even when DECAWM is disabled.</summary>
    private void FindTextSizingWritePosition(int width)
    {
        while (true)
        {
            if (_isPendingWrap || _cursorCol + width > Cols)
            {
                if (Modes.IsAutoWrapMode || LowerTextSizingEnd(Math.Max(0, Cols - width), width) >= 0)
                {
                    _viewport[_cursorRow].SetSoftWrap(_cursorCol);
                    _cursorCol = 0;
                    AdvanceCursorRowForLineFeed();
                }
                else _cursorCol = Cols - width;
                _isPendingWrap = false;
            }
            int end = LowerTextSizingEnd(_cursorCol, width);
            if (end < 0) return;
            _cursorCol = end;
        }
    }

    private int LowerTextSizingEnd(int col, int width)
    {
        int end = -1;
        for (int i = col; i < Math.Min(Cols, col + width); i++)
            if (_viewport[_cursorRow].GetTextSizing(i) is { RowOffset: > 0 } part)
                end = Math.Max(end, i - part.ColumnOffset + part.Block.Width);
        return end;
    }

    private void EraseTextSizingAcrossBoundary(int row)
    {
        if (_textSizingBlocks == null || _textSizingBlocks.Count == 0) return;
        for (int col = 0; col < Cols; col++)
            if (GetTextSizingAbsolute(col, row) is { RowOffset: > 0 } piece)
                EraseTextSizingBlock(piece.Block, col - piece.ColumnOffset, row - piece.RowOffset);
    }

    private bool TryAppendToSizedBlock(ReadOnlySpan<char> text)
    {
        int col = _lastCharRow == _cursorRow ? _lastCharCol : -1;
        var piece = SizedBlockAtCursorEnd(col);
        if (piece == null)
        {
            col = _isPendingWrap ? _cursorCol : _cursorCol - 1;
            piece = SizedBlockAtCursorEnd(col);
        }
        if (piece == null || !UnicodeWidth.ShouldAttachToPrevious(piece.Block.Text, text, _isAfterZwj)) return false;
        string merged = string.Concat(piece.Block.Text.AsSpan(), text);
        if (!TextSizingOptions.IsSafeText(merged)) return true;
        var replacement = new TextSizingBlock(merged, piece.Block.Options, piece.Block.BaseWidth);
        int x = col - piece.ColumnOffset;
        int y = TextSizingAbsoluteRow(_cursorRow) - piece.RowOffset;
        if (!ReserveTextSizing(replacement, x, y)) return true;
        ReplaceTextSizingBlock(piece.Block, replacement, x, y);
        _isAfterZwj = EndsWithZwj(text);
        return true;
    }

    private CellTextSizing? SizedBlockAtCursorEnd(int col)
    {
        if (col < 0 || col >= Cols) return null;
        var piece = _viewport[_cursorRow].GetTextSizing(col);
        if (piece == null || piece.RowOffset != 0) return null;
        int end = col - piece.ColumnOffset + piece.Block.Width;
        bool pending = Modes.IsAutoWrapMode && end >= Cols;
        return _cursorCol == Math.Min(Cols - 1, end) && _isPendingWrap == pending ? piece : null;
    }

    private void ReplaceTextSizingBlock(TextSizingBlock previous, TextSizingBlock replacement, int x, int y)
    {
        bool historyChanged = false;
        for (int row = Math.Max(0, y); row < y + previous.Height; row++)
            for (int col = x; col < x + previous.Width; col++)
            {
                if (!_isAltScreen && row < _scrollback.Count)
                {
                    _scrollback.ReplaceTextSizingCell(row, col, previous, replacement);
                    historyChanged = true;
                    continue;
                }
                ReplaceSizedRowCell(GetRowAbsolute(row), col, previous, replacement);
            }
        if (historyChanged) Array.Clear(_cachedRenderRowIds);
        if (_textSizingBlocks?.Remove(previous) == true) _textSizingStorage -= previous.StorageCost;
    }

    private static void ReplaceSizedRowCell(TerminalRow? target, int col,
        TextSizingBlock previous, TextSizingBlock replacement)
    {
        if (target?.GetTextSizing(col) is not { } piece || !ReferenceEquals(piece.Block, previous)) return;
        target.SetTextSizing(col, new(replacement, piece.ColumnOffset, piece.RowOffset));
        if (piece.IsAnchor)
        {
            target.Cells[col].HasExtendedText = true;
            target.SetExtendedText(col, replacement.Text);
        }
        target.TouchRevision();
    }

    private void PrepareTextSizingOverwrite(int width)
    {
        FindTextSizingWritePosition(width);
        int row = TextSizingAbsoluteRow(_cursorRow);
        EraseTextSizingRange(_cursorCol, _cursorCol + width, row, row + 1);
    }

    private void EraseTextSizingForShift(bool insert, int count)
    {
        if (_textSizingBlocks == null || count <= 0) return;
        int row = TextSizingAbsoluteRow(_cursorRow);
        for (int col = _cursorCol; col < Cols; col++)
        {
            if (_viewport[_cursorRow].GetTextSizing(col) is not { } piece) continue;
            bool split = insert ? col >= Cols - count || (col == _cursorCol && piece.ColumnOffset > 0)
                : col < _cursorCol + count;
            if (piece.Block.Height > 1 || split)
                EraseTextSizingBlock(piece.Block, col - piece.ColumnOffset, row - piece.RowOffset);
        }
    }

    private HashSet<TextSizingBlock> TextSizingInRange(int x0, int x1, int y0, int y1)
    {
        var result = new HashSet<TextSizingBlock>();
        for (int row = Math.Max(0, y0); row < y1; row++)
            for (int col = Math.Max(0, x0); col < Math.Min(Cols, x1); col++)
                if (GetTextSizingAbsolute(col, row) is { } part) result.Add(part.Block);
        return result;
    }

    private bool ReserveTextSizing(TextSizingBlock block, int x, int y, bool allowReclaim = true)
    {
        long cost = block.StorageCost;
        if (_textSizingStorage + cost > MaxTextSizingStorage)
        {
            if (_textSizingPruneRequired || _textSizingPrunedHistory != (_scrollback.Generation, _scrollback.TotalRowsEvicted))
                PruneTextSizingStorage();
            long reclaimed = allowReclaim ? TextSizingInRange(x, x + block.Width, y, y + block.Height).Sum(b => b.StorageCost) : 0;
            if (_textSizingStorage - reclaimed + cost > MaxTextSizingStorage) return false;
        }
        (_textSizingBlocks ??= new()).Add(block);
        _textSizingStorage += cost;
        return true;
    }

    private void PruneTextSizingStorage()
    {
        var retained = new HashSet<TextSizingBlock>();
        void Add(Storage.SmallMap<CellTextSizing>? map) => map?.ForEach((_, piece) => retained.Add(piece.Block));
        foreach (var row in _isAltScreen ? _mainScreen : _altScreen) Add(row.GetTextSizingMap());
        foreach (var row in _viewport) Add(row.GetTextSizingMap());
        for (int i = 0; i < _scrollback.Count; i++) Add(_scrollback.GetTextSizingMap(i));
        _textSizingBlocks = retained;
        _textSizingStorage = retained.Sum(b => b.StorageCost);
        _textSizingPruneRequired = false;
        _textSizingPrunedHistory = (_scrollback.Generation, _scrollback.TotalRowsEvicted);
    }

    private void EraseTextSizingRange(int x0, int x1, int y0, int y1)
    {
        if (_textSizingBlocks == null || _textSizingBlocks.Count == 0) return;
        var anchors = new Dictionary<TextSizingBlock, (int X, int Y)>();
        for (int row = Math.Max(0, y0); row < y1; row++)
            for (int col = Math.Max(0, x0); col < Math.Min(Cols, x1); col++)
                if (GetTextSizingAbsolute(col, row) is { } piece)
                    anchors.TryAdd(piece.Block, (col - piece.ColumnOffset, row - piece.RowOffset));
        foreach (var entry in anchors) EraseTextSizingBlock(entry.Key, entry.Value.X, entry.Value.Y);
    }

    private void EraseTextSizingBlock(TextSizingBlock block, int x, int y)
    {
        var blank = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
        bool changedHistory = false;
        int limit = _isAltScreen ? Rows : _scrollback.Count + Rows;
        for (int row = Math.Max(0, y); row < Math.Min(limit, y + block.Height); row++)
        {
            for (int col = Math.Max(0, x); col < Math.Min(Cols, x + block.Width); col++)
            {
                if (!_isAltScreen && row < _scrollback.Count)
                {
                    _scrollback.ClearTextSizingCell(row, col, blank, block);
                    changedHistory = true;
                    continue;
                }
                EraseSizedRowCell(GetRowAbsolute(row), col, block, blank);
            }
        }
        if (changedHistory) Array.Clear(_cachedRenderRowIds);
        if (_textSizingBlocks?.Remove(block) == true) _textSizingStorage -= block.StorageCost;
    }
    private static void EraseSizedRowCell(TerminalRow? target, int col, TextSizingBlock block, TerminalCell blank)
    {
        if (target == null || !ReferenceEquals(target.GetTextSizing(col)?.Block, block)) return;
        target.Cells[col] = blank;
        target.SetExtendedText(col, null);
        target.SetHyperlink(col, null);
        target.SetUnderline(col, null);
        target.SetTextSizing(col, null);
        target.TouchRevision();
    }

}
