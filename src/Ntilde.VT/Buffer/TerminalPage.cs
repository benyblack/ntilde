using System;
using System.Buffers;

namespace Ntilde.VT.Storage
{
    /// <summary>
    /// A contiguous slab of TerminalCell values for multiple scrollback rows.
    /// Eliminates per-row heap objects by packing many rows into one flat array.
    ///
    /// The backing <see cref="Cells"/> array is rented from
    /// <see cref="ArrayPool{T}.Shared"/> to avoid repeated large allocations.
    /// Call <see cref="ReturnToPool"/> when the page is no longer needed so the
    /// array is returned to the pool.
    /// </summary>
    public sealed partial class TerminalPage
    {
        /// <summary>Number of rows this page can hold.</summary>
        public readonly int RowsInPage;

        /// <summary>Number of columns per row.</summary>
        public readonly int Cols;

        /// <summary>
        /// Flat cell storage rented from <see cref="ArrayPool{T}.Shared"/>.
        /// Length may be >= RowsInPage * Cols (pool over-allocation is normal).
        /// Only use indices [0, RowsInPage * Cols).
        /// </summary>
        public readonly TerminalCell[] Cells;

        /// <summary>How many rows in this page contain actual data (0 … RowsInPage).</summary>
        public int UsedRows;

        private bool _returned;

        /// <summary>Returns true if this page has been returned to the pool and should not be accessed.</summary>
        public bool IsReturned => _returned;

        private ulong _isWrappedMask;
        // Zero means full-row/legacy; otherwise the value is the content column count plus one.
        private int[]? _wrappedColumns;
        private int[]? _writtenColumns;

        // Sparse per-row metadata side-channels. null = no extended text/hyperlinks on any row in this page.
        // Indexed by rowIndex (0..RowsInPage-1). Each element is null if that row has no metadata.
        private SmallMap<string>?[]? _extendedText;
        private SmallMap<Ntilde.VT.Links.Hyperlink>?[]? _hyperlinks;
        private SmallMap<Ntilde.VT.CellUnderline>?[]? _underlines;

        public TerminalPage(int rowsInPage, int cols)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(rowsInPage);
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cols);

            RowsInPage = rowsInPage;
            Cols = cols;
            // Rent — pool may give us a slightly larger array; that's fine.
            Cells = ArrayPool<TerminalCell>.Shared.Rent(rowsInPage * cols);
            UsedRows = 0;
            _returned = false;

            // Pre-fill the usable portion with default cells.
            ResetCells();
        }

        /// <summary>Returns true when no more rows can be appended to this page.</summary>
        public bool IsFull => UsedRows >= RowsInPage;

        /// <summary>
        /// Byte size of the cell data in the usable part of the array,
        /// used for budget tracking.
        /// </summary>
        public int ByteSize => RowsInPage * Cols * TerminalPageConstants.CellBytes;

        // ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns a span over the cells of the given row within this page.
        /// </summary>
        public Span<TerminalCell> GetRowSpan(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
            return Cells.AsSpan(rowIndex * Cols, Cols);
        }

        /// <summary>
        /// Returns a read-only span over the cells of the given row within this page.
        /// </summary>
        public ReadOnlySpan<TerminalCell> GetRowSpanReadOnly(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));
            return Cells.AsSpan(rowIndex * Cols, Cols);
        }

        /// <summary>
        /// Resets a single row to all-default cells.
        /// </summary>
        public void ClearRow(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage)
                throw new ArgumentOutOfRangeException(nameof(rowIndex));

            var def = TerminalCell.Default;
            var span = Cells.AsSpan(rowIndex * Cols, Cols);
            for (int i = 0; i < span.Length; i++)
                span[i] = def;
        }

        /// <summary>
        /// <summary>Resets all rows to default cells and sets UsedRows to 0.
        /// Called by the pool before returning the page for reuse.
        /// </summary>
        public void Reset()
        {
            UsedRows = 0;
            _returned = false;
            _isWrappedMask = 0;
            _wrappedColumns = null;
            _writtenColumns = null;
            _extendedText = null;
            _hyperlinks = null;
            _underlines = null;
            _textSizing = null;
            ResetCells();
        }

        /// <summary>
        /// Returns the backing <see cref="Cells"/> array to <see cref="ArrayPool{T}.Shared"/>.
        /// The page must not be used after this call.
        /// </summary>
        public void ReturnToPool()
        {
            if (_returned) return;
            _returned = true;
            _isWrappedMask = 0;
            _wrappedColumns = null;
            _writtenColumns = null;
            _extendedText = null;
            _hyperlinks = null;
            _underlines = null;
            _textSizing = null;
            // Clear the usable portion so pooled memory doesn't retain stale data.
            Cells.AsSpan(0, RowsInPage * Cols).Clear();
            ArrayPool<TerminalCell>.Shared.Return(Cells, clearArray: false);
        }

        public bool IsRowWrapped(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage) throw new ArgumentOutOfRangeException(nameof(rowIndex));
            return (_isWrappedMask & (1UL << rowIndex)) != 0;
        }

        public void SetRowWrapped(int rowIndex, bool wrapped) => SetRowWrapped(rowIndex, wrapped, null);

        internal void SetRowWrapped(int rowIndex, bool wrapped, int? wrappedColumns)
        {
            if ((uint)rowIndex >= (uint)RowsInPage) throw new ArgumentOutOfRangeException(nameof(rowIndex));
            if (wrappedColumns is < 0 || wrappedColumns > Cols || (wrappedColumns.HasValue && !wrapped))
                throw new ArgumentOutOfRangeException(nameof(wrappedColumns));
            if (wrapped) _isWrappedMask |= (1UL << rowIndex);
            else _isWrappedMask &= ~(1UL << rowIndex);
            if (wrappedColumns is int columns && columns < Cols)
            {
                _wrappedColumns ??= new int[RowsInPage];
                _wrappedColumns[rowIndex] = columns + 1;
            }
            else if (_wrappedColumns != null) _wrappedColumns[rowIndex] = 0;
        }

        public int? GetWrappedColumns(int rowIndex)
        {
            if ((uint)rowIndex >= (uint)RowsInPage) throw new ArgumentOutOfRangeException(nameof(rowIndex));
            int encoded = _wrappedColumns?[rowIndex] ?? 0;
            return encoded == 0 ? null : encoded - 1;
        }

        internal int GetWrittenColumns(int rowIndex) => _writtenColumns?[rowIndex] ?? Cols;

        internal void SetWrittenColumns(int rowIndex, int columns)
        {
            if (columns < 0 || columns > Cols) throw new ArgumentOutOfRangeException(nameof(columns));
            if (columns == Cols && _writtenColumns == null) return;
            if (_writtenColumns == null)
            {
                _writtenColumns = new int[RowsInPage];
                Array.Fill(_writtenColumns, Cols);
            }
            _writtenColumns[rowIndex] = columns;
        }

        // ── Extended Text Metadata ───────────────────────────────────────────────

        /// <summary>Gets the extended text (grapheme cluster) for a cell, or null if none.</summary>
        public string? GetExtendedText(int rowIndex, int col)
        {
            if (_extendedText == null) return null;
            var map = _extendedText[rowIndex];
            if (map == null) return null;
            return map.TryGet(col, out var text) ? text : null;
        }

        /// <summary>Sets the extended text for a cell in the specified row.</summary>
        public void SetExtendedText(int rowIndex, int col, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            _extendedText ??= new SmallMap<string>?[RowsInPage];
            _extendedText[rowIndex] ??= new SmallMap<string>();
            _extendedText[rowIndex]!.Set(col, text);
        }

        /// <summary>Copies all extended text entries from an existing SmallMap into the specified row.</summary>
        public void SetExtendedTextFromMap(int rowIndex, SmallMap<string>? source)
        {
            if (source == null || source.Count == 0) return;
            _extendedText ??= new SmallMap<string>?[RowsInPage];
            _extendedText[rowIndex] = source;
        }

        /// <summary>Returns the raw SmallMap for the row's extended text (null if none).</summary>
        public SmallMap<string>? GetExtendedTextMap(int rowIndex) =>
            _extendedText?[rowIndex];

        // ── Hyperlink Metadata ───────────────────────────────────────────────────

        /// <summary>Gets the hyperlink identity for a cell, or null if none.</summary>
        public Ntilde.VT.Links.Hyperlink? GetHyperlink(int rowIndex, int col)
        {
            if (_hyperlinks == null) return null;
            var map = _hyperlinks[rowIndex];
            if (map == null) return null;
            return map.TryGet(col, out var link) ? link : null;
        }

        /// <summary>Copies all hyperlink entries from an existing SmallMap into the specified row.</summary>
        public void SetHyperlinkFromMap(int rowIndex, SmallMap<Ntilde.VT.Links.Hyperlink>? source)
        {
            if (source == null || source.Count == 0) return;
            _hyperlinks ??= new SmallMap<Ntilde.VT.Links.Hyperlink>?[RowsInPage];
            _hyperlinks[rowIndex] = source;
        }

        /// <summary>Returns the raw SmallMap for the row's hyperlinks (null if none).</summary>
        public SmallMap<Ntilde.VT.Links.Hyperlink>? GetHyperlinkMap(int rowIndex) =>
            _hyperlinks?[rowIndex];

        /// <summary>Installs a row's underline side table (#270); the page takes ownership of the map.</summary>
        public void SetUnderlineFromMap(int rowIndex, SmallMap<Ntilde.VT.CellUnderline>? source)
        {
            if (source == null || source.Count == 0) return;
            _underlines ??= new SmallMap<Ntilde.VT.CellUnderline>?[RowsInPage];
            _underlines[rowIndex] = source;
        }

        /// <summary>Returns the raw SmallMap for the row's underline styles and colors (null if none).</summary>
        public SmallMap<Ntilde.VT.CellUnderline>? GetUnderlineMap(int rowIndex) =>
            _underlines?[rowIndex];

        /// <summary>
        /// Clears a row's wrap flag and side tables. Must be called when a row
        /// slot is vacated (pop) so a later append into the same slot cannot
        /// resurrect stale metadata.
        /// </summary>
        public void ClearRowMetadata(int rowIndex)
        {
            SetRowWrapped(rowIndex, false);
            SetWrittenColumns(rowIndex, Cols);
            if (_extendedText != null) _extendedText[rowIndex] = null;
            if (_hyperlinks != null) _hyperlinks[rowIndex] = null;
            if (_underlines != null) _underlines[rowIndex] = null;
            if (_textSizing != null) _textSizing[rowIndex] = null;
        }

        /// <summary>
        /// Updates cells that rely on default foreground or background colors to a new theme.
        /// Explicit colors are left untouched, even when they equal the old default. This is an
        /// O(n) operation over all used cells, invoked rarely during a theme change.
        /// </summary>
        public void UpdateThemeDefaults(uint newFg, uint newBg)
        {
            var span = Cells.AsSpan(0, UsedRows * Cols);
            for (int i = 0; i < span.Length; i++)
            {
                if (span[i].IsDefaultForeground)
                {
                    span[i].Fg = newFg;
                }
                if (span[i].IsDefaultBackground)
                {
                    span[i].Bg = newBg;
                }
            }
        }

        // ── Private ──────────────────────────────────────────────────────────────

        private void ResetCells()
        {
            var def = TerminalCell.Default;
            var span = Cells.AsSpan(0, RowsInPage * Cols);
            for (int i = 0; i < span.Length; i++)
                span[i] = def;
        }
    }

    internal static class TerminalPageConstants
    {
        /// <summary>Measured size of TerminalCell in bytes (char + ushort + uint + uint = 12 B).</summary>
        public const int CellBytes = 12;

        /// <summary>Number of rows stored per page. 64 rows balances allocation granularity vs. overhead.</summary>
        public const int DefaultRowsPerPage = 64;

        /// <summary>Number of pages to pre-warm per terminal instance.</summary>
        public const int PreheatPagesPerInstance = 4;
    }
}
