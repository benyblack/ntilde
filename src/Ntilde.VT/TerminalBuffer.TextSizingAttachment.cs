using System.Text;
using Ntilde.VT.Links;

namespace Ntilde.VT;

internal readonly record struct TextSizingCellStyle(TerminalCell Cell, Hyperlink? Hyperlink, CellUnderline? Underline)
{
    internal TerminalCell ForPiece(CellTextSizing piece)
    {
        var cell = Cell;
        cell.Character = piece.IsAnchor ? piece.Block.Text[0] : ' ';
        cell.IsWide = false;
        cell.IsWideContinuation = false;
        cell.HasExtendedText = piece.IsAnchor && piece.Block.Text.Length > 1;
        cell.IsDirty = true;
        return cell;
    }
}

public partial class TerminalBuffer
{
    private void WriteAutoSizedGrapheme(string grapheme, TextSizingOptions options)
    {
        int width = NaturalTextSizingWidth(grapheme);
        if (width <= 0)
        {
            if (!TryAppendToSizedBlock(grapheme)) WriteContentCore(grapheme);
            return;
        }
        if (options.Scale == 1 && !options.IsFractional && !HasEmojiSizingBase(grapheme)) WriteContentCore(grapheme);
        else WriteSizedBlock(grapheme, options, width);
    }

    private static int AttachedTextSizingWidth(TextSizingBlock previous, string merged)
    {
        if (previous.Options.Width != 0) return previous.BaseWidth;
        int width = NaturalTextSizingWidth(merged);
        return width is >= 1 and <= 7 ? width : previous.BaseWidth;
    }

    private static bool HasEmojiSizingBase(ReadOnlySpan<char> text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (UnicodeWidth.IsWideEmojiPresentationBase(rune) || UnicodeWidth.IsTextDefaultEmoji(rune.Value)) return true;
        return false;
    }

    // Keep OSC 66 selector handling independent of chunk boundaries, including complete payloads.
    private static int NaturalTextSizingWidth(ReadOnlySpan<char> text)
    {
        int width = UnicodeWidth.GetGraphemeWidth(text);
        if (width != 2 || text.IndexOf('\uFE0E') < 0) return width;
        Rune previous = default;
        bool reduced = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (UnicodeWidth.IsVariationSelector15(rune))
            {
                if (UnicodeWidth.IsWideEmojiPresentationBase(previous)) reduced = true;
            }
            else if (UnicodeWidth.IsVariationSelector16(rune) || UnicodeWidth.GetRuneWidth(rune) > 0) reduced = false;
            previous = rune;
        }
        return reduced ? 1 : width;
    }

    private bool ReserveTextSizingReplacement(TextSizingBlock previous, TextSizingBlock replacement)
    {
        if (_textSizingStorage - previous.StorageCost + replacement.StorageCost > MaxTextSizingStorage
            && (_textSizingPruneRequired || _textSizingPrunedHistory != (_scrollback.Generation, _scrollback.TotalRowsEvicted)))
            PruneTextSizingStorage();
        if (_textSizingStorage - previous.StorageCost + replacement.StorageCost > MaxTextSizingStorage) return false;
        (_textSizingBlocks ??= new()).Add(replacement);
        _textSizingStorage += replacement.StorageCost;
        return true;
    }

    private bool ResizeAttachedTextSizing(CellTextSizing piece, TextSizingBlock replacement, int col, int x, int y)
    {
        if (replacement.Width > Cols || replacement.Height > Rows) return true;
        var row = _viewport[_cursorRow];
        var style = new TextSizingCellStyle(row.Cells[col], row.GetHyperlink(col), row.GetUnderline(col));
        int originalTop = y - TextSizingAbsoluteRow(0);
        bool followsEnd = _cursorRow == originalTop && _cursorCol == Math.Min(Cols - 1, x + piece.Block.Width)
            && _isPendingWrap == (Modes.IsAutoWrapMode && x + piece.Block.Width >= Cols);
        if (!ReserveTextSizingReplacement(piece.Block, replacement)) return true;
        EraseTextSizingBlock(piece.Block, x, y);
        if (x + replacement.Width > Cols)
        {
            if (Modes.IsAutoWrapMode) { x = 0; y++; }
            else x = Cols - replacement.Width;
        }
        y = FitAttachedTextSizingHeight(originalTop, y, replacement.Height);
        EraseTextSizingRange(x, x + replacement.Width, y, y + replacement.Height);
        StoreAttachedTextSizing(replacement, x, y, style);
        if (followsEnd)
        {
            _cursorCol = x;
            FinishTextSizingWrite(y - TextSizingAbsoluteRow(0), replacement.Width, replacement.Text);
        }
        _isAfterZwj = EndsWithZwj(replacement.Text);
        return true;
    }

    private int FitAttachedTextSizingHeight(int originalTop, int absoluteTop, int height)
    {
        int top = absoluteTop - TextSizingAbsoluteRow(0);
        if (originalTop >= ScrollTop && originalTop <= ScrollBottom && height <= ScrollBottom - ScrollTop + 1)
        {
            int scroll = Math.Max(0, top + height - 1 - ScrollBottom);
            for (int i = 0; i < scroll; i++) ScrollUpInternal();
            top -= scroll;
        }
        else top = Math.Min(top, Rows - height);
        return TextSizingAbsoluteRow(top);
    }

    private void StoreAttachedTextSizing(TextSizingBlock block, int x, int y, TextSizingCellStyle style)
    {
        bool historyChanged = false;
        int historyCount = _isAltScreen ? 0 : _scrollback.Count;
        for (int dy = 0; dy < block.Height; dy++)
        {
            int absoluteRow = y + dy;
            if (absoluteRow < 0 || absoluteRow >= historyCount + Rows) continue;
            for (int dx = 0; dx < block.Width; dx++)
            {
                var part = new CellTextSizing(block, dx, dy);
                if (absoluteRow < historyCount)
                {
                    _scrollback.WriteTextSizingCell(absoluteRow, x + dx, part, style);
                    historyChanged = true;
                }
                else StoreAttachedViewportSizing(_viewport[absoluteRow - historyCount], x + dx, part, style);
            }
        }
        if (historyChanged) InvalidateTextSizingHistory();
    }

    private static void StoreAttachedViewportSizing(TerminalRow row, int col, CellTextSizing piece, TextSizingCellStyle style)
    {
        row.Cells[col] = style.ForPiece(piece);
        row.SetExtendedText(col, piece.IsAnchor ? piece.Block.Text : null);
        row.SetHyperlink(col, style.Hyperlink);
        row.SetUnderline(col, style.Underline);
        row.SetTextSizing(col, piece);
        row.NoteColumnsWritten(col + 1);
        row.TouchRevision();
    }
}
