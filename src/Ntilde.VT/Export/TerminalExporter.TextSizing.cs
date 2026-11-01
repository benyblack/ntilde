using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Ntilde.VT.Export;

public static partial class TerminalExporter
{
    private sealed record ClippedSizing(TextSizingBlock Block, int X, int Y,
        TerminalCell Cell, CellUnderline? Underline);

    // Recreate missing anchors before the normal viewport walk, including after history eviction.
    // The temporary origin and bounded scrolling allow different block heights to share row zero.
    private static void AppendClippedSizingPrelude(StringBuilder output, TerminalBuffer buffer, int screenTop)
    {
        var blocks = new List<ClippedSizing>();
        for (int col = 0; col < buffer.Cols; col++)
        {
            var piece = buffer.GetTextSizingAbsolute(col, screenTop);
            if (piece is not { ColumnOffset: 0, RowOffset: > 0 }) continue;
            blocks.Add(new(piece.Block, col, -piece.RowOffset,
                buffer.GetCellAbsolute(col, screenTop), buffer.GetUnderlineAbsolute(col, screenTop)));
        }
        if (blocks.Count == 0) return;
        blocks.Sort(static (a, b) => a.Y.CompareTo(b.Y));
        int origin = blocks[0].Y;
        foreach (var block in blocks)
        {
            int row = block.Y - origin;
            int scroll = Math.Max(0, row + block.Block.Height - buffer.Rows);
            if (scroll > 0)
            {
                output.Append(CultureInfo.InvariantCulture, $"\x1b[{scroll}S");
                origin += scroll;
                row -= scroll;
            }
            output.Append(CultureInfo.InvariantCulture, $"\x1b[{row + 1};{block.X + 1}H\x1b[0m");
            AppendSgrSequence(output, block.Cell, block.Underline, TerminalCell.Default, null);
            AnsiCellWriter.AppendTextSizing(output, block.Block);
        }
        if (origin < 0) output.Append(CultureInfo.InvariantCulture, $"\x1b[{-origin}S");
        output.Append("\x1b[1;1H\x1b[0m");
    }
}
