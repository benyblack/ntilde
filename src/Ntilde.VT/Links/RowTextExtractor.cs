using System.Collections.Generic;
using System.Text;

namespace Ntilde.VT.Links
{
    /// <summary>
    /// Turns an absolute buffer row into its display text plus a map from each text-character
    /// index to the originating terminal column. Wide-cell continuations are skipped; extended
    /// grapheme text contributes multiple characters that all map to its starting column.
    /// </summary>
    public static class RowTextExtractor
    {
        public static (string Text, int[] CharToCol) Extract(TerminalBuffer buffer, int absRow)
            => Extract(buffer, absRow, out _);

        /// <summary>Captures start/end columns under one lock; ordinary rows reuse the same array.</summary>
        public static (string Text, int[] CharToCol) Extract(TerminalBuffer buffer, int absRow, out int[] charToEndCol)
        {
            charToEndCol = System.Array.Empty<int>();
            if (buffer == null) return (string.Empty, System.Array.Empty<int>());

            var sb = new StringBuilder();
            var map = new List<int>();
            List<int>? ends = null;

            buffer.Lock.EnterReadLock();
            try
            {
                if (absRow < 0 || absRow >= buffer.TotalLines) return (string.Empty, System.Array.Empty<int>());

                int cols = buffer.Cols;
                for (int col = 0; col < cols; col++)
                {
                    var cell = buffer.GetCellAbsolute(col, absRow);
                    var sizing = buffer.GetTextSizingAbsolute(col, absRow);
                    if (cell.IsWideContinuation || sizing is { ColumnOffset: > 0 }) continue;
                    int endCol = sizing == null ? col : col + sizing.Block.Width - 1;
                    if (endCol > col) ends ??= new List<int>(map);

                    // Links occupy the whole rendered rectangle, including its lower rows.
                    string g = sizing?.Block.Text ?? buffer.GetGraphemeAbsolute(col, absRow);
                    if (string.IsNullOrEmpty(g)) g = " ";
                    foreach (char ch in g)
                    {
                        sb.Append(ch);
                        map.Add(col);
                        ends?.Add(endCol);
                    }
                }
            }
            finally { buffer.Lock.ExitReadLock(); }

            int[] starts = map.ToArray();
            charToEndCol = ends?.ToArray() ?? starts;
            return (sb.ToString(), starts);
        }

        /// <summary>Maps a char-range LinkSpan to an inclusive [StartCol, EndCol] column range.</summary>
        public static (int StartCol, int EndCol) SpanToColumns(LinkSpan span, int[] charToCol)
            => SpanToColumns(span, charToCol, charToCol);

        public static (int StartCol, int EndCol) SpanToColumns(LinkSpan span, int[] charToCol, int[] charToEndCol)
        {
            int startCol = charToCol[span.StartChar];
            int endCol = charToEndCol[span.EndChar - 1];
            return (startCol, endCol);
        }
    }
}
