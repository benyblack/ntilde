using System;
using System.Text;

namespace Ntilde.VT.Export
{
    /// <summary>
    /// Serialises <see cref="RenderCellSnapshot"/> rows back to ANSI text, for a renderer that paints a
    /// terminal into another terminal (the mux text client, Phase 3 spec §6.3). Pure text generation:
    /// no I/O, no cursor movement, no erase - the caller positions each row.
    /// </summary>
    /// <remarks>
    /// Deliberately separate from <see cref="TerminalExporter.ExportToAnsi"/>, whose exact output (a
    /// newline-joined, trimmed whole-screen dump over TerminalCells) the "Export Snapshot (ANSI)"
    /// command relies on. A style change is written as a full <c>SGR 0;…</c>, never a delta: a
    /// few more bytes, no state to get wrong.
    /// </remarks>
    public static class AnsiCellWriter
    {
        public const string Reset = "\x1b[0m";

        /// <summary>
        /// Appends columns [0, <paramref name="maxCols"/>) of one row and returns how many columns it
        /// wrote. Continuation halves are skipped (the wide cell before them covers them); a wide cell
        /// whose second half would fall at or past <paramref name="maxCols"/>, and an orphan
        /// continuation, are written as a space so the column count stays exact. Ends with SGR 0.
        /// </summary>
        public static int AppendRow(StringBuilder sb, ReadOnlySpan<RenderCellSnapshot> cells, int maxCols)
        {
            ArgumentNullException.ThrowIfNull(sb);
            int limit = Math.Min(maxCols, cells.Length);
            bool haveStyle = false;
            RenderCellSnapshot current = default;
            int col = 0;
            while (col < limit)
            {
                RenderCellSnapshot cell = cells[col];
                bool wide = cell.IsWide && !cell.IsWideContinuation;
                bool clippedToSpace = cell.IsWideContinuation || (wide && col + 1 >= limit);
                int width = clippedToSpace ? 1 : (wide ? 2 : 1);

                if (!haveStyle || !SameStyle(current, cell))
                {
                    AppendSgr(sb, cell);
                    current = cell;
                    haveStyle = true;
                }

                // Append the char directly rather than via cell.Text ?? cell.Character.ToString():
                // this is the text client's repaint hot path, and .ToString() would allocate a new
                // string for every plain (non-grapheme) cell in the row.
                if (clippedToSpace)
                {
                    sb.Append(' ');
                }
                else if (cell.Text is not null)
                {
                    sb.Append(cell.Text);
                }
                else if (cell.Character == '\0')
                {
                    sb.Append(' ');
                }
                else
                {
                    sb.Append(cell.Character);
                }

                col += width;
            }

            if (col > 0) sb.Append(Reset);
            return col;
        }

        /// <summary>The cell's complete style as one <c>ESC [ 0 ; … m</c>.</summary>
        public static void AppendSgr(StringBuilder sb, in RenderCellSnapshot cell)
        {
            ArgumentNullException.ThrowIfNull(sb);
            sb.Append("\x1b[0");
            if (cell.IsBold) sb.Append(";1");
            if (cell.IsFaint) sb.Append(";2");
            if (cell.IsItalic) sb.Append(";3");
            if (cell.IsUnderline) sb.Append(";4");
            if (cell.IsBlink) sb.Append(";5");
            if (cell.IsInverse) sb.Append(";7");
            if (cell.IsHidden) sb.Append(";8");
            if (cell.IsStrikethrough) sb.Append(";9");
            if (!cell.IsDefaultForeground) AppendColor(sb, cell.FgIndex, cell.Foreground, foreground: true);
            if (!cell.IsDefaultBackground) AppendColor(sb, cell.BgIndex, cell.Background, foreground: false);
            sb.Append('m');
        }

        public static bool SameStyle(in RenderCellSnapshot a, in RenderCellSnapshot b) =>
            a.IsBold == b.IsBold && a.IsFaint == b.IsFaint && a.IsItalic == b.IsItalic && a.IsUnderline == b.IsUnderline
            && a.IsBlink == b.IsBlink && a.IsInverse == b.IsInverse && a.IsHidden == b.IsHidden && a.IsStrikethrough == b.IsStrikethrough
            && a.IsDefaultForeground == b.IsDefaultForeground && a.IsDefaultBackground == b.IsDefaultBackground
            && a.FgIndex == b.FgIndex && a.BgIndex == b.BgIndex
            && (a.IsDefaultForeground || a.FgIndex >= 0 || a.Foreground == b.Foreground)
            && (a.IsDefaultBackground || a.BgIndex >= 0 || a.Background == b.Background);

        private static void AppendColor(StringBuilder sb, short index, TermColor rgb, bool foreground)
        {
            if (index >= 0 && index < 8)
            {
                sb.Append(';').Append((foreground ? 30 : 40) + index);
            }
            else if (index >= 8 && index < 16)
            {
                sb.Append(';').Append((foreground ? 90 : 100) + index - 8);
            }
            else if (index >= 16)
            {
                sb.Append(foreground ? ";38;5;" : ";48;5;").Append(index);
            }
            else
            {
                sb.Append(foreground ? ";38;2;" : ";48;2;").Append(rgb.R).Append(';').Append(rgb.G).Append(';').Append(rgb.B);
            }
        }
    }
}
