namespace Ntilde.VT;

public partial class TerminalBuffer
{
    // A replay checkpoint describes the visible state, independently of seek history.
    // Preserve retained blocks while they fit; under pressure retire old rectangles whole
    // before reserving the validated checkpoint, rather than dropping its visible glyphs.
    private void MakeRoomForLegacySizing(List<TerminalTextSizingState> entries)
    {
        long required = entries.Sum(entry => DecodeTextSizing(entry).StorageCost);
        if (HasSizingCapacity(required)) return;
        ReclaimSizingHistory(required);
        if (!HasSizingCapacity(required)) ReclaimSizingRows(_isAltScreen ? _mainScreen : _altScreen, required);
        if (!HasSizingCapacity(required)) ReclaimSizingRows(_viewport, required);
    }

    private bool HasSizingCapacity(long required) => _textSizingStorage <= MaxTextSizingStorage - required;

    private void ReclaimSizingHistory(long required)
    {
        for (int row = 0; row < _scrollback.Count && !HasSizingCapacity(required); row++)
        {
            var map = _scrollback.GetTextSizingMap(row);
            if (map == null || map.Count == 0) continue;
            var anchors = new Dictionary<TextSizingBlock, (int X, int Y)>();
            int currentRow = row;
            map.ForEach((col, piece) => anchors.TryAdd(piece.Block,
                (col - piece.ColumnOffset, currentRow - piece.RowOffset)));
            foreach (var (block, position) in anchors)
            {
                if (HasSizingCapacity(required)) return;
                // Scrollback belongs to the main plane even while the alternate screen is active.
                EraseTextSizingBlockInPlane(block, position.X, position.Y, alternate: false);
            }
        }
    }

    // Main history remains retained when the alternate screen is resized.
    // Retire invalid rectangles across both history and the detached main viewport.
    private void PrepareMainHistoryTextSizingResize(int cols, int rows)
    {
        var discarded = new Dictionary<TextSizingBlock, (int X, int Y)>();
        for (int row = 0; row < _scrollback.Count; row++)
        {
            int currentRow = row;
            _scrollback.GetTextSizingMap(row)?.ForEach((col, piece) =>
            {
                int x = col - piece.ColumnOffset;
                if (x + piece.Block.Width > cols || piece.Block.Height > rows)
                    discarded.TryAdd(piece.Block, (x, currentRow - piece.RowOffset));
            });
        }
        foreach (var (block, position) in discarded)
            EraseTextSizingBlockInPlane(block, position.X, position.Y, alternate: false);
    }

    private void ReclaimSizingRows(TerminalRow[] rows, long required)
    {
        var discarded = new HashSet<TextSizingBlock>();
        long projected = _textSizingStorage;
        foreach (var row in rows)
            row.GetTextSizingMap()?.ForEach((_, piece) =>
            {
                if (projected > MaxTextSizingStorage - required && discarded.Add(piece.Block))
                    projected -= piece.Block.StorageCost;
            });
        ClearDiscardedSizingRows(rows, discarded);
        foreach (var block in discarded)
            if (_textSizingBlocks?.Remove(block) == true) _textSizingStorage -= block.StorageCost;
    }
}
