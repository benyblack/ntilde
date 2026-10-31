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
            => AppendRowCore(sb, cells, maxCols, null);

        /// <summary>Resolve session color overrides into SGR without changing the outer terminal's palette.</summary>
        public static int AppendRow(StringBuilder sb, ReadOnlySpan<RenderCellSnapshot> cells, int maxCols, in RenderThemeSnapshot theme)
            => AppendRowCore(sb, cells, maxCols, theme);

        private static int AppendRowCore(StringBuilder sb, ReadOnlySpan<RenderCellSnapshot> cells, int maxCols, RenderThemeSnapshot? theme)
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
                int width = wide && !clippedToSpace ? 2 : 1;

                if (!haveStyle || !SameStyle(current, cell))
                {
                    AppendSgrCore(sb, cell, theme);
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
        public static void AppendSgr(StringBuilder sb, in RenderCellSnapshot cell) => AppendSgrCore(sb, cell, null);

        private static void AppendSgrCore(StringBuilder sb, in RenderCellSnapshot cell, RenderThemeSnapshot? theme)
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
            AppendEffectiveColor(sb, cell.IsDefaultForeground, cell.FgIndex, cell.Foreground, true, theme);
            AppendEffectiveColor(sb, cell.IsDefaultBackground, cell.BgIndex, cell.Background, false, theme);
            sb.Append('m');
            if (cell.IsUnderline && cell.Underline is { } underline) AppendUnderlineSgrCore(sb, underline, theme);
        }

        /// <summary>
        /// A styled or colored underline (#270) as its own <c>ESC [ 4:n ; 58:... m</c>, written after the SGR that
        /// turns the underline on.
        /// </summary>
        /// <remarks>
        /// Colon form, in a sequence of its own: a terminal that rejects colon sub-parameters drops only this
        /// sequence and still shows the plain underline. The semicolon form of 58 would have a terminal that
        /// doesn't know 58 read the color's numbers as SGR codes of their own (a 0 among them resets).
        /// </remarks>
        public static void AppendUnderlineSgr(StringBuilder sb, CellUnderline underline) => AppendUnderlineSgrCore(sb, underline, null);

        private static void AppendUnderlineSgrCore(StringBuilder sb, CellUnderline underline, RenderThemeSnapshot? theme)
        {
            ArgumentNullException.ThrowIfNull(sb);
            ArgumentNullException.ThrowIfNull(underline);
            bool styled = underline.Style != UnderlineStyle.Single;
            // A straight underline in the text color takes nothing past the 4 of the main SGR. Written, it would be
            // an empty ESC [ m: SGR 0, a reset.
            if (!styled && !underline.HasColor) return;

            sb.Append("\x1b[");
            if (styled) sb.Append("4:").Append((int)underline.Style);
            if (underline.HasColor)
            {
                if (styled) sb.Append(';');
                bool overridePalette = theme is { } colors && colors.PaletteOverrides.Contains(underline.ColorIndex);
                if (underline.ColorIndex >= 0 && !overridePalette)
                {
                    sb.Append("58:5:").Append(underline.ColorIndex);
                }
                else
                {
                    // 58:2:<color space id>:r:g:b, the id left empty as xterm and kitty write it.
                    TermColor rgb = overridePalette ? theme!.Value.AnsiPalette[underline.ColorIndex] : underline.Color.GetValueOrDefault();
                    sb.Append("58:2:").Append(':').Append(rgb.R).Append(':').Append(rgb.G).Append(':').Append(rgb.B);
                }
            }

            sb.Append('m');
        }

        public static bool SameStyle(in RenderCellSnapshot a, in RenderCellSnapshot b) =>
            a.IsBold == b.IsBold && a.IsFaint == b.IsFaint && a.IsItalic == b.IsItalic && a.IsUnderline == b.IsUnderline
            && a.IsBlink == b.IsBlink && a.IsInverse == b.IsInverse && a.IsHidden == b.IsHidden && a.IsStrikethrough == b.IsStrikethrough
            && a.Underline == b.Underline
            && a.IsDefaultForeground == b.IsDefaultForeground && a.IsDefaultBackground == b.IsDefaultBackground
            && a.FgIndex == b.FgIndex && a.BgIndex == b.BgIndex
            && (a.IsDefaultForeground || a.FgIndex >= 0 || a.Foreground == b.Foreground)
            && (a.IsDefaultBackground || a.BgIndex >= 0 || a.Background == b.Background);

        private static void AppendEffectiveColor(StringBuilder sb, bool isDefault, short index, TermColor literal,
            bool foreground, RenderThemeSnapshot? theme)
        {
            if (theme is { } colors)
            {
                bool overrideDefault = isDefault && (foreground ? colors.HasForegroundOverride : colors.HasBackgroundOverride);
                if (overrideDefault)
                {
                    AppendColor(sb, -1, foreground ? colors.Foreground : colors.Background, foreground);
                    return;
                }
                if (!isDefault && colors.PaletteOverrides.Contains(index))
                {
                    AppendColor(sb, -1, colors.AnsiPalette[index], foreground);
                    return;
                }
            }
            if (!isDefault) AppendColor(sb, index, literal, foreground);
        }

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
