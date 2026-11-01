using System.Runtime.InteropServices;
using Ntilde.VT.Storage;

namespace Ntilde.VT;

public partial class TerminalBuffer
{
    private const string TextSizingValidationName = "text sizing";
    /// <summary>Bounded active-screen rectangles for legacy buffer-only replay checkpoints. Lock held.</summary>
    public List<TerminalTextSizingState>? ExportReplayTextSizing()
    {
        AssertLockHeld();
        if (_textSizingBlocks == null) return null;
        var state = new TerminalStateSnapshot { Cols = Cols, Rows = Rows };
        var seen = new HashSet<TextSizingBlock>();
        for (int r = 0; r < _viewport.Length; r++)
            AppendSizingState(_viewport[r].GetTextSizingMap(), r, false, seen, state);
        return state.TextSizing;
    }

    private static void ValidateReplayTextSizing(ReplaySnapshot snapshot, byte[]? cells)
    {
        ValidateTextSizingState(ReplaySizingEnvelope(snapshot), cells, null, null);
        if (snapshot.IsAltScreen && snapshot.TextSizing?.Any(entry => entry.Y < 0) == true)
            throw StateTransferValidation.Reject(TextSizingValidationName, "alternate-screen anchor above row zero");
    }

    private static TerminalStateSnapshot ReplaySizingEnvelope(ReplaySnapshot snapshot)
        => new()
        {
            Cols = snapshot.Cols,
            Rows = snapshot.Rows,
            TextSizing = snapshot.TextSizing,
            Main = new TerminalScreenState { ExtendedText = snapshot.ExtendedText }
        };

    private void ExportTextSizingState(TerminalStateSnapshot state, TerminalRow[] main, TerminalRow[] alt)
    {
        if (_textSizingBlocks == null) return;
        var seen = new HashSet<TextSizingBlock>();
        int first = _scrollback.Count - state.ScrollbackRowCount;
        for (int r = first; r < _scrollback.Count; r++)
            AppendSizingState(_scrollback.GetTextSizingMap(r), r - first, false, seen, state);
        for (int r = 0; r < Math.Min(main.Length, Rows); r++)
            AppendSizingState(main[r].GetTextSizingMap(), r + state.ScrollbackRowCount, false, seen, state);
        for (int r = 0; r < Math.Min(alt.Length, Rows); r++)
            AppendSizingState(alt[r].GetTextSizingMap(), r, true, seen, state);
    }

    private static void AppendSizingState(SmallMap<CellTextSizing>? map, int row, bool alt,
        HashSet<TextSizingBlock> seen, TerminalStateSnapshot state)
    {
        if (map == null) return;
        map.ForEach((col, piece) =>
        {
            if (!seen.Add(piece.Block)) return;
            int x = col - piece.ColumnOffset, y = row - piece.RowOffset;
            int height = alt ? state.Rows : state.Rows + state.ScrollbackRowCount;
            if (x < 0 || x + piece.Block.Width > state.Cols || y + piece.Block.Height > height) return;
            (state.TextSizing ??= []).Add(new TerminalTextSizingState
            {
                Text = piece.Block.Text,
                Options = piece.Block.Options,
                BaseWidth = piece.Block.BaseWidth,
                X = x,
                Y = y,
                Alt = alt
            });
        });
    }

    private static void ValidateTextSizingState(TerminalStateSnapshot state, byte[]? main, byte[]? alt, byte[]? history)
    {
        if (state.TextSizing == null) return;
        if (state.TextSizing.Count > MaxTextSizingStorage / 128)
            throw StateTransferValidation.Reject(TextSizingValidationName, "too many blocks");
        long storage = 0;
        var occupied = new HashSet<(bool Alt, int Row, int Col)>();
        foreach (var entry in state.TextSizing)
        {
            var block = DecodeTextSizing(entry);
            storage += block.StorageCost;
            if (storage > MaxTextSizingStorage) throw StateTransferValidation.Reject(TextSizingValidationName, "storage budget exceeded");
            ValidateSizingRectangle(state, entry, block);
            ValidateSizingCells(state, entry, block, (main, alt, history), occupied);
        }
    }

    private static void ValidateSizingRectangle(TerminalStateSnapshot state, TerminalTextSizingState entry, TextSizingBlock block)
    {
        int rows = entry.Alt ? state.Rows : checked(state.Rows + state.ScrollbackRowCount);
        if (entry.X < 0 || entry.X > state.Cols - block.Width || entry.Y < (entry.Alt ? 0 : 1 - block.Height)
            || entry.Y >= rows || entry.Y > rows - block.Height)
            throw StateTransferValidation.Reject(TextSizingValidationName, "rectangle outside retained geometry");
    }

    private static void ValidateSizingCells(TerminalStateSnapshot state, TerminalTextSizingState entry,
        TextSizingBlock block, (byte[]? Main, byte[]? Alt, byte[]? History) bytes,
        HashSet<(bool Alt, int Row, int Col)> occupied)
    {
        for (int dy = 0; dy < block.Height; dy++)
        {
            int r = entry.Y + dy;
            if (r < 0) continue;
            var (screen, cells, localRow) = ResolveSizingSource(state, bytes, entry.Alt, r);
            for (int dx = 0; dx < block.Width; dx++)
            {
                int c = entry.X + dx;
                if (!occupied.Add((entry.Alt, r, c))) throw StateTransferValidation.Reject(TextSizingValidationName, "overlapping rectangles");
                ValidateSizingCell(screen, cells, localRow * state.Cols + c, block, dx == 0 && dy == 0);
            }
        }
    }

    private static (TerminalScreenState Screen, byte[]? Cells, int Row) ResolveSizingSource(
        TerminalStateSnapshot state, (byte[]? Main, byte[]? Alt, byte[]? History) bytes, bool alt, int row)
    {
        if (alt) return (state.Alt, bytes.Alt, row);
        if (row < state.ScrollbackRowCount) return (state.Scrollback, bytes.History, row);
        return (state.Main, bytes.Main, row - state.ScrollbackRowCount);
    }

    private static TextSizingBlock DecodeTextSizing(TerminalTextSizingState? entry)
    {
        if (entry == null || !entry.Options.IsValid || entry.BaseWidth is < 1 or > 7
            || !TextSizingOptions.IsSafeText(entry.Text)
            || entry.Options.Width > 0 && entry.Options.Width != entry.BaseWidth)
            throw StateTransferValidation.Reject(TextSizingValidationName, "invalid block");
        return new TextSizingBlock(entry.Text, entry.Options, entry.BaseWidth);
    }

    private static void ValidateSizingCell(TerminalScreenState screen, byte[]? bytes, int key,
        TextSizingBlock block, bool anchor)
    {
        if (bytes == null || (uint)key >= (uint)(bytes.Length / Marshal.SizeOf<TerminalCell>()))
            throw StateTransferValidation.Reject(TextSizingValidationName, "missing occupied cells");
        var cell = MemoryMarshal.Cast<byte, TerminalCell>(bytes)[key];
        string? text = cell.HasExtendedText ? screen.ExtendedText?.GetValueOrDefault(key) : null;
        string actual = text ?? cell.Character.ToString();
        if (cell.IsWide || cell.IsWideContinuation || actual != (anchor ? block.Text : " "))
            throw StateTransferValidation.Reject(TextSizingValidationName, "cell content disagrees with rectangle");
    }

    private void ImportTextSizingState(TerminalStateSnapshot state, bool activeOnly = false)
    {
        if (activeOnly && _textSizingBlocks != null) PruneTextSizingStorage();
        else
        {
            _textSizingBlocks = null;
            _textSizingStorage = 0;
        }
        if (state.TextSizing == null) return;
        if (activeOnly) MakeRoomForLegacySizing(state.TextSizing);
        foreach (var entry in state.TextSizing)
        {
            var block = DecodeTextSizing(entry);
            bool fits = entry.X + block.Width <= Cols && entry.Y + block.Height <= Rows + (entry.Alt ? 0 : state.ScrollbackRowCount);
            if (fits && activeOnly)
                fits = ReserveTextSizing(block, entry.X, TextSizingAbsoluteRow(entry.Y), allowReclaim: false);
            else if (fits)
            {
                (_textSizingBlocks ??= []).Add(block);
                _textSizingStorage += block.StorageCost;
            }
            RestoreSizingRectangle(state, entry, block, fits, activeOnly);
        }
    }

    private void RestoreSizingRectangle(TerminalStateSnapshot state, TerminalTextSizingState entry,
        TextSizingBlock block, bool fits, bool activeOnly)
    {
        for (int dy = 0; dy < block.Height; dy++)
            for (int dx = 0; dx < block.Width; dx++)
                RestoreSizingPiece(state, entry, new CellTextSizing(block, dx, dy), fits, activeOnly);
    }

    private void RestoreSizingPiece(TerminalStateSnapshot state, TerminalTextSizingState entry,
        CellTextSizing piece, bool fits, bool activeOnly)
    {
        int r = entry.Y + piece.RowOffset, c = entry.X + piece.ColumnOffset;
        if (r < 0 || c >= Cols) return;
        if (!entry.Alt && r < state.ScrollbackRowCount)
        {
            int retainedRow = r - (state.ScrollbackRowCount - _scrollback.Count);
            if (retainedRow < 0) return;
            if (fits) _scrollback.SetTextSizingCell(retainedRow, c, piece);
            else _scrollback.ClearImportedTextSizingCell(retainedRow, c);
            return;
        }
        int localRow = entry.Alt ? r : r - state.ScrollbackRowCount;
        var rows = entry.Alt ? _altScreen : _mainScreen;
        if (activeOnly) rows = _viewport;
        if ((uint)localRow >= (uint)rows.Length) return;
        if (fits) rows[localRow].SetTextSizing(c, piece);
        else
        {
            ClearSizedCell(rows[localRow], c);
        }
    }
}
