namespace Ntilde.VT.Storage;

public sealed partial class ScrollbackPages
{
    internal SmallMap<CellTextSizing>? GetTextSizingMap(int logicalIndex)
    {
        var (page, row) = FindPage(logicalIndex);
        return page.GetTextSizingMap(row);
    }

    internal void ClearImportedTextSizingCell(int logicalRow, int col)
    {
        var (page, row) = FindPage(logicalRow);
        page.GetRowSpan(row)[col] = TerminalCell.Default;
        page.GetExtendedTextMap(row)?.Remove(col);
        page.GetHyperlinkMap(row)?.Remove(col);
        page.GetUnderlineMap(row)?.Remove(col);
    }

    internal void SetTextSizingCell(int logicalRow, int col, CellTextSizing piece)
    {
        var (page, row) = FindPage(logicalRow);
        var map = page.GetTextSizingMap(row) ?? new SmallMap<CellTextSizing>();
        map.Set(col, piece);
        page.SetTextSizingFromMap(row, map);
    }

    internal void WriteTextSizingCell(int logicalRow, int col, CellTextSizing piece, TextSizingCellStyle style)
    {
        var (page, row) = FindPage(logicalRow);
        page.GetRowSpan(row)[col] = style.ForPiece(piece);
        page.SetExtendedTextFromMap(row, SizingMetadata(page.GetExtendedTextMap(row), col, piece.IsAnchor ? piece.Block.Text : null));
        page.SetHyperlinkFromMap(row, SizingMetadata(page.GetHyperlinkMap(row), col, style.Hyperlink));
        page.SetUnderlineFromMap(row, SizingMetadata(page.GetUnderlineMap(row), col, style.Underline));
        SetTextSizingCell(logicalRow, col, piece);
        page.SetWrittenColumns(row, Math.Max(page.GetWrittenColumns(row), col + 1));
        if (page.IsRowWrapped(row) && page.GetWrappedColumns(row) is int width && width < col + 1)
            page.SetRowWrapped(row, true, col + 1);
    }

    private static SmallMap<T>? SizingMetadata<T>(SmallMap<T>? map, int col, T? value) where T : class
    {
        if (value == null) { map?.Remove(col); return map; }
        (map ??= new()).Set(col, value);
        return map;
    }

    internal long AppendRow(TerminalRow row)
        => AppendRow(row.Cells, row.IsWrapped,
            new(row.GetExtendedTextMap(), row.GetHyperlinkMap(), row.GetUnderlineMap(), row.GetTextSizingMap()),
            row.WrappedColumns, row.WrittenColumns);

    /// <summary>Editing any visible piece can erase a block whose anchor is in retained history.</summary>
    internal void ClearTextSizingCell(int logicalRow, int col, TerminalCell replacement, TextSizingBlock block)
    {
        var (page, row) = FindPage(logicalRow);
        var map = page.GetTextSizingMap(row);
        if (map?.TryGet(col, out CellTextSizing? piece) != true || piece == null || !ReferenceEquals(piece.Block, block)) return;
        page.GetRowSpan(row)[col] = replacement;
        page.GetExtendedTextMap(row)?.Remove(col);
        page.GetHyperlinkMap(row)?.Remove(col);
        page.GetUnderlineMap(row)?.Remove(col);
        map.Remove(col);
    }
    internal void ReplaceTextSizingCell(int logicalRow, int col, TextSizingBlock previous, TextSizingBlock replacement)
    {
        var (page, row) = FindPage(logicalRow);
        var map = page.GetTextSizingMap(row);
        if (map?.TryGet(col, out var piece) != true || piece == null || !ReferenceEquals(piece.Block, previous)) return;
        map.Set(col, new(replacement, piece.ColumnOffset, piece.RowOffset));
        if (!piece.IsAnchor) return;
        page.GetRowSpan(row)[col].HasExtendedText = true;
        var extended = page.GetExtendedTextMap(row) ?? new SmallMap<string>();
        extended.Set(col, replacement.Text);
        page.SetExtendedTextFromMap(row, extended);
    }

}
