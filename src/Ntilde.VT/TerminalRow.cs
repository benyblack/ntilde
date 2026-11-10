using System.Collections.Generic;

namespace Ntilde.VT
{
    public partial class TerminalRow
    {
        private static long _nextId;
        public readonly long Id;

        public TerminalCell[] Cells;
        // If true, this line ends because it wrapped automatically.
        // If false, it ends because of an explicit newline (or end of buffer).
        private bool _isWrapped;
        private int? _wrappedColumns;
        public bool IsWrapped
        {
            get => _isWrapped;
            set { _isWrapped = value; _wrappedColumns = null; }
        }

        /// <summary>
        /// Content columns before a soft wrap, excluding unused wide-character padding.
        /// Null retains the legacy full-row interpretation. Actual spaces remain content.
        /// </summary>
        public int? WrappedColumns
        {
            get => _wrappedColumns is int columns && columns < Cells.Length && CanExcludeWrapTail(columns) ? columns : null;
            internal set
            {
                if (value is < 0 || value > Cells.Length)
                    throw new System.ArgumentOutOfRangeException(nameof(value));
                if (value.HasValue && !_isWrapped)
                    throw new System.InvalidOperationException("Wrap columns require a soft-wrapped row.");
                if (value is int columns && columns < Cells.Length && !CanExcludeWrapTail(columns))
                    throw new System.InvalidOperationException("Wrapped columns would hide existing cell content.");
                _wrappedColumns = value == Cells.Length ? null : value;
            }
        }

        /// <summary>Conservative extent of authored cells; keeps typed spaces distinct from unused padding.</summary>
        public int WrittenColumns { get; internal set; }

        internal void SetSoftWrap(int columns)
        {
            IsWrapped = true;
            if (columns < 0 || columns > Cells.Length)
                throw new System.ArgumentOutOfRangeException(nameof(columns));
            if (columns >= WrittenColumns && columns < Cells.Length && CanExcludeWrapTail(columns)) _wrappedColumns = columns;
        }

        internal static bool IsWrapPaddingCell(TerminalCell cell)
        {
            const ushort allowed = (ushort)(TerminalCellFlags.DefaultForeground | TerminalCellFlags.DefaultBackground | TerminalCellFlags.Dirty);
            return cell.Character is ' ' or '\0' && cell.IsDefaultBackground && (cell.Flags & ~allowed) == 0;
        }

        private bool CanExcludeWrapTail(int columns)
        {
            for (int col = columns; col < Cells.Length; col++)
                if (!IsWrapPaddingCell(Cells[col]) || GetExtendedText(col) != null || GetHyperlink(col) != null || GetUnderline(col) != null || GetTextSizing(col) != null)
                    return false;
            return true;
        }

        internal bool HasWrapPadding => _wrappedColumns.HasValue;

        internal void NoteColumnsWritten(int endExclusive)
        {
            if (endExclusive > WrittenColumns) WrittenColumns = System.Math.Min(endExclusive, Cells.Length);
            if (_wrappedColumns is int width && endExclusive > width)
                _wrappedColumns = endExclusive < Cells.Length ? endExclusive : null;
        }

        public uint Revision { get; set; }
        public void TouchRevision() => Revision++;

        // M2.2: Side-table for extended graphemes (strings)
        private Storage.SmallMap<string>? _extendedText;

        // #95 gap 2: holds link identity, not just the URI. Cells in the same logical OSC 8 anchor share
        // one Hyperlink reference, so grouping is reference equality — see Links/Hyperlink.cs.
        private Storage.SmallMap<Links.Hyperlink>? _hyperlinks;

        // #270: underline style and color, for the cells whose underline is more than a straight line in the
        // text color. Cells written with one pen share one CellUnderline instance.
        private Storage.SmallMap<CellUnderline>? _underlines;

        // DECSCA: which columns hold characters DECSED/DECSERA must not erase. Sparse like the other
        // side tables - a row that never saw protected text carries no map at all - and deliberately not a
        // TerminalCellFlags bit: protection is not SGR styling, and every flag bit is already taken.
        private Storage.SmallMap<CellProtection>? _protected;

        public string? GetExtendedText(int col)
        {
            if (_extendedText == null) return null;
            return _extendedText.TryGet(col, out var text) ? text : null;
        }

        public void SetExtendedText(int col, string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                _extendedText?.Remove(col);
                if (_extendedText?.Count == 0) _extendedText = null;
                return;
            }
            _extendedText ??= new Storage.SmallMap<string>();
            _extendedText.Set(col, text);
            NoteColumnsWritten(col + 1);
        }

        public void ClearExtendedText()
        {
            _extendedText = null;
        }

        public Links.Hyperlink? GetHyperlink(int col)
        {
            if (_hyperlinks == null) return null;
            return _hyperlinks.TryGet(col, out var link) ? link : null;
        }

        public void SetHyperlink(int col, Links.Hyperlink? link)
        {
            if (link is null)
            {
                _hyperlinks?.Remove(col);
                if (_hyperlinks?.Count == 0) _hyperlinks = null;
                return;
            }

            _hyperlinks ??= new Storage.SmallMap<Links.Hyperlink>();
            _hyperlinks.Set(col, link);
            NoteColumnsWritten(col + 1);
        }

        public void ClearHyperlinks()
        {
            _hyperlinks = null;
        }

        public CellUnderline? GetUnderline(int col)
        {
            if (_underlines == null) return null;
            return _underlines.TryGet(col, out var underline) ? underline : null;
        }

        public void SetUnderline(int col, CellUnderline? underline)
        {
            if (underline is null)
            {
                _underlines?.Remove(col);
                if (_underlines?.Count == 0) _underlines = null;
                return;
            }

            _underlines ??= new Storage.SmallMap<CellUnderline>();
            _underlines.Set(col, underline);
            NoteColumnsWritten(col + 1);
        }

        public void ClearUnderlines()
        {
            _underlines = null;
        }

        /// <summary>
        /// Whether the character at <paramref name="col"/> was written under DECSCA 1 and is still protected. A wide glyph is protected
        /// as a unit, held on its anchor: a continuation is protected exactly when it still follows a protected wide anchor, and an
        /// anchor only while its continuation (or the screen edge) follows it. A half that an overwrite, erase, shift or clip has
        /// orphaned therefore reads unprotected without any cleanup at the place that orphaned it.
        /// </summary>
        public bool IsProtected(int col)
        {
            if (_protected == null || (uint)col >= (uint)Cells.Length) return false;
            var cell = Cells[col];
            if (cell.IsWideContinuation) return col > 0 && Cells[col - 1].IsWide && _protected.TryGet(col - 1, out _);
            if (!_protected.TryGet(col, out _)) return false;
            return !cell.IsWide || col == Cells.Length - 1 || Cells[col + 1].IsWideContinuation;
        }

        /// <summary>True when any column of this row reads protected (see <see cref="IsProtected"/>).</summary>
        public bool HasProtectedCells => _protected is { Count: > 0 } map && map.AnyKey(this, static (row, col) => row.IsProtected(col));

        /// <summary>Cheap: whether the row holds any protection record at all, possibly stale. Safe to use to skip protection work.</summary>
        internal bool HasProtectionEntries => _protected != null;

        /// <summary>Marks or unmarks one column; unmarking a row that never had protection is free.</summary>
        public void SetProtected(int col, bool isProtected)
        {
            if (!isProtected)
            {
                if (_protected == null) return;
                _protected.Remove(col);
                if (_protected.Count == 0) _protected = null;
                return;
            }

            _protected ??= new Storage.SmallMap<CellProtection>();
            _protected.Set(col, CellProtection.Instance);
            NoteColumnsWritten(col + 1);
        }

        /// <summary>After a copy that kept the first <paramref name="copiedCols"/> columns, a wide anchor on the last kept column lost its continuation and its protection with it.</summary>
        internal void ClearProtectionOfClippedWideAnchor(int copiedCols, int sourceCols)
        {
            if (copiedCols > 0 && copiedCols < sourceCols && Cells[copiedCols - 1].IsWide) SetProtected(copiedCols - 1, false);
        }

        public void ClearProtection()
        {
            _protected = null;
        }

        /// <summary>The raw protected-column map; for state export, not for mutation.</summary>
        public Storage.SmallMap<CellProtection>? GetProtectionMap() => _protected;

        /// <summary>
        /// Returns the raw SmallMap backing extended text (grapheme clusters) for this row.
        /// This is intended for preservation into paged scrollback — do not cache or mutate.
        /// </summary>
        public Storage.SmallMap<string>? GetExtendedTextMap() => _extendedText;

        /// <summary>
        /// Returns the raw SmallMap backing hyperlinks for this row.
        /// This is intended for preservation into paged scrollback — do not cache or mutate.
        /// </summary>
        public Storage.SmallMap<Links.Hyperlink>? GetHyperlinkMap() => _hyperlinks;

        /// <summary>
        /// Returns the raw SmallMap backing underline styles and colors for this row.
        /// This is intended for preservation into paged scrollback; do not cache or mutate.
        /// </summary>
        public Storage.SmallMap<CellUnderline>? GetUnderlineMap() => _underlines;

        /// <summary>
        /// True when this row carries any side-table entry. Lets callers on the write hot path
        /// (insert mode runs per printable character) skip metadata maintenance entirely for
        /// the overwhelmingly common plain-ASCII, no-hyperlink row.
        /// </summary>
        public bool HasRowMetadata => _extendedText != null || _hyperlinks != null || _underlines != null || _textSizing != null || _protected != null;

        /// <summary>
        /// Moves side-table entries to follow a horizontal cell shift, so extended graphemes and
        /// hyperlinks stay attached to the cells they describe.
        /// </summary>
        /// <param name="startCol">First column affected — the cursor column for ICH/DCH.</param>
        /// <param name="delta">
        /// Positive to shift right (ICH / insert mode), negative to shift left (DCH).
        /// </param>
        /// <param name="cols">Row width; entries pushed past it are dropped.</param>
        /// <remarks>
        /// Without this, <c>CSI @</c> / <c>CSI P</c> moved <see cref="Cells"/> but left the maps
        /// keyed to pre-shift columns: cells kept <c>HasExtendedText</c> while their strings —
        /// and their hyperlinks — pointed at the wrong columns (issue #164, item 1).
        /// </remarks>
        public void ShiftRowMetadata(int startCol, int delta, int cols) => ShiftRowMetadata(startCol, delta, cols, cols);

        /// <summary>
        /// Shifts the side-table entries of columns <paramref name="startCol"/>..<paramref name="end"/>-1 by <paramref name="delta"/>,
        /// dropping those that leave that span; columns at or past <paramref name="end"/> (right of a right margin) stay where they are.
        /// </summary>
        public void ShiftRowMetadata(int startCol, int delta, int cols, int end)
        {
            if (delta == 0) return;
            end = Math.Clamp(end, 0, cols);
            _extendedText = ShiftMap(_extendedText, startCol, delta, end);
            _hyperlinks = ShiftMap(_hyperlinks, startCol, delta, end);
            _underlines = ShiftMap(_underlines, startCol, delta, end);
            _textSizing = ShiftMap(_textSizing, startCol, delta, end);
            _protected = ShiftMap(_protected, startCol, delta, end);
        }

        // Generic over the value type so the extended-text and hyperlink maps share one implementation:
        // they must shift identically, and two copies of this logic is how they would drift apart.
        private static Storage.SmallMap<T>? ShiftMap<T>(
            Storage.SmallMap<T>? map, int startCol, int delta, int cols) where T : class
        {
            if (map is null || map.Count == 0) return null;

            // Snapshot before rebuilding: shifting in place would revisit entries that had already
            // been relocated (and, for a right shift, overwrite ones not yet visited).
            int n = map.Count;
            var keys = new int[n];
            var values = new T[n];
            int i = 0;
            map.ForEach((col, value) =>
            {
                keys[i] = col;
                values[i] = value;
                i++;
            });

            Storage.SmallMap<T>? shifted = null;
            for (int e = 0; e < n; e++)
            {
                int col = keys[e];
                int dest = col;
                if (col >= startCol && col < cols)
                {
                    dest = col + delta;

                    // Dropped: pushed off the end of the row (ICH), or consumed by the deletion
                    // itself (DCH moves the deleted columns to below startCol). Columns left of
                    // startCol are untouched and must not be range-checked against startCol.
                    if (dest < startCol || dest >= cols) continue;
                }

                shifted ??= new Storage.SmallMap<T>();
                shifted.Set(dest, values[e]);
            }

            return shifted;
        }

        /// <summary>
        /// Copies <paramref name="source"/>'s side-table entries for columns below
        /// <paramref name="columnLimit"/> onto this row, leaving higher columns untouched.
        /// </summary>
        /// <remarks>
        /// Only valid where columns map one-to-one — the resize paths that copy cells straight
        /// across rather than reflowing them (the alt screen, and detached screen buffers). Reflow
        /// re-columns its content and has to map each cell individually instead.
        ///
        /// Without this, those paths rebuilt rows from <see cref="Cells"/> alone, so a resize while
        /// an alt-screen application was up dropped every extended grapheme and OSC 8 link on
        /// screen — the same loss as reflow's, on the no-reflow path (#164).
        /// </remarks>
        public void CopyRowMetadataFrom(TerminalRow source, int columnLimit)
        {
            if (!source.HasRowMetadata || columnLimit <= 0) return;

            source._extendedText?.ForEach((col, text) =>
            {
                if (col < columnLimit) SetExtendedText(col, text);
            });
            source._hyperlinks?.ForEach((col, link) =>
            {
                if (col < columnLimit) SetHyperlink(col, link);
            });
            source._underlines?.ForEach((col, underline) =>
            {
                if (col < columnLimit) SetUnderline(col, underline);
            });
            source._textSizing?.ForEach((col, value) =>
            {
                if (col < columnLimit) SetTextSizing(col, value);
            });
            source._protected?.ForEach((col, _) =>
            {
                if (col < columnLimit) SetProtected(col, true);
            });
        }

        /// <summary>
        /// Installs side tables wholesale. Counterpart of the Get*Map accessors:
        /// used when a row is restored from paged scrollback (height grow) so
        /// extended graphemes, hyperlinks and underline styles survive the round trip. The row
        /// takes ownership of the maps.
        /// </summary>
        public void RestoreSideTables(
            Storage.SmallMap<string>? extendedText,
            Storage.SmallMap<Links.Hyperlink>? hyperlinks,
            Storage.SmallMap<CellUnderline>? underlines,
            Storage.SmallMap<CellTextSizing>? textSizing = null,
            Storage.SmallMap<CellProtection>? protectedCells = null)
        {
            _extendedText = extendedText is { Count: > 0 } ? extendedText : null;
            _hyperlinks = hyperlinks is { Count: > 0 } ? hyperlinks : null;
            _underlines = underlines is { Count: > 0 } ? underlines : null;
            _textSizing = textSizing is { Count: > 0 } ? textSizing : null;
            _protected = protectedCells is { Count: > 0 } ? protectedCells : null;
        }

        public TerminalRow(int cols)
        {
            Id = System.Threading.Interlocked.Increment(ref _nextId);
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++) Cells[i] = TerminalCell.Default;
        }

        public TerminalRow(int cols, TermColor fg, TermColor bg)
        {
            Id = System.Threading.Interlocked.Increment(ref _nextId);
            Cells = new TerminalCell[cols];
            for (int i = 0; i < cols; i++)
            {
                // Initialize as Default so they update when theme changes
                Cells[i] = new TerminalCell(' ', fg, bg, false, false, true, true);
            }
        }
    }
}
