namespace Ntilde.VT
{
    /// <summary>The underline styles SGR 4:x selects (21 is <see cref="Double"/>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifier contains type name",
        Justification = "Named as the SGR 4:x styles they select are: single, double, curly, dotted, dashed.")]
    public enum UnderlineStyle : byte
    {
        Single = 1,
        Double = 2,
        Curly = 3,
        Dotted = 4,
        Dashed = 5,
    }

    /// <summary>
    /// What sets an underline apart from a plain one: its style (SGR 4:x, 21) and its own color (SGR 58),
    /// either an RGB value or a palette index that, like the foreground's, follows the theme.
    /// </summary>
    /// <remarks>
    /// Kept in <see cref="TerminalRow"/>'s side table, not in <see cref="TerminalCell"/>: the cell's flags
    /// are full, and widening every cell (scrollback included) for an attribute few cells carry is the
    /// wrong trade (#270). Whether a cell is underlined at all is still <see cref="TerminalCellFlags.Underline"/>;
    /// a straight underline in the text color has no entry, and only the cells that need one carry this.
    /// Cells written with one pen share one instance, as a hyperlink run shares one identity.
    /// </remarks>
    /// <param name="Style">The underline's style.</param>
    /// <param name="Color">The SGR 58 RGB color; <c>null</c> when it is a palette index or unset (the text color).</param>
    /// <param name="ColorIndex">The SGR 58 palette index, or -1.</param>
    public sealed record CellUnderline(UnderlineStyle Style, TermColor? Color = null, short ColorIndex = -1)
    {
        /// <summary>True when SGR 58 gave the underline a color of its own.</summary>
        public bool HasColor => Color.HasValue || ColorIndex >= 0;

        /// <summary>
        /// The side-table entry for a pen, or <c>null</c> for a straight underline in the text color: that one
        /// needs nothing beyond the cell's flag.
        /// </summary>
        public static CellUnderline? For(UnderlineStyle style, TermColor? color, short colorIndex)
            => style == UnderlineStyle.Single && !color.HasValue && colorIndex < 0
                ? null
                : new CellUnderline(style, colorIndex >= 0 ? null : color, colorIndex);
    }
}
