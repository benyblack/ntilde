using System;
using System.Collections.Generic;
using System.Threading;

namespace Ntilde.VT.Storage
{
    /// <summary>
    /// Metrics snapshot for <see cref="ScrollbackPages"/>.
    /// </summary>
    public readonly struct ScrollbackMetrics
    {
        /// <summary>Number of rows currently retained in the scrollback.</summary>
        public int RowCount { get; init; }

        /// <summary>Number of page objects currently in use (rented from the pool).</summary>
        public int ActivePages { get; init; }

        /// <summary>Number of pages waiting in the pool, ready for reuse.</summary>
        public int PooledPages { get; init; }

        /// <summary>Total bytes consumed by live cell data.</summary>
        public long BytesUsed { get; init; }

        /// <summary>Configured byte budget (maximum allowed).</summary>
        public long MaxBytes { get; init; }
    }

    /// <summary>
    /// Page-based scrollback store that replaces the per-row <see cref="CircularBuffer{TerminalRow}"/>.
    ///
    /// Rows are packed into <see cref="TerminalPage"/> slabs (default 64 rows each), so adding
    /// 10,000 scrollback lines allocates ~157 page objects instead of 10,000 <c>TerminalRow</c>
    /// objects. Each page's backing <c>TerminalCell[]</c> is rented from
    /// <see cref="System.Buffers.ArrayPool{T}.Shared"/> for further allocation reuse.
    ///
    /// Memory is bounded by <see cref="MaxScrollbackBytes"/>. When the budget is exceeded the
    /// oldest pages are evicted and returned to the <see cref="TerminalPagePool"/>.
    /// </summary>
    public sealed class ScrollbackPages
    {
        // ── Configuration ────────────────────────────────────────────────────────
        /// <summary>Maximum bytes of cell data to retain. Default: 128 MB.</summary>
        public long MaxScrollbackBytes { get; set; }

        // ── Internals ────────────────────────────────────────────────────────────
        private readonly int _cols;
        private readonly int _rowsPerPage;
        private readonly TerminalPagePool _pool;

        /// <summary>
        /// Ordered list of pages, oldest first.
        /// <see cref="LinkedList{T}"/> gives O(1) front eviction.
        /// </summary>
        private readonly LinkedList<TerminalPage> _pages = new();

        private long _currentBytes;

        // Absolute row counters (never reset on eviction so indexing stays correct).
        // They ARE reset by Clear(), which is what the generation epoch below exists
        // to make detectable.
        private long _totalRowsAppended;
        private long _totalRowsEvicted;

        // Invalidation epoch for the absolute-row coordinate space. Eviction leaves the
        // space intact (TotalRowsEvicted only grows), but Clear() resets both counters to
        // zero, so absolute row ids issued before it silently alias rows appended after it.
        // Clear() is reachable from CSI 3J / RIS / user "clear buffer" / reflow, all of
        // which are routine, so a holder of an absolute row id needs a way to notice.
        // The counter is process-global rather than per-instance because reflow can swap in
        // a brand-new ScrollbackPages (TerminalBuffer.ReflowEngine): a per-instance counter
        // starting at zero would collide with a pre-reflow id's epoch.
        private static long s_nextGeneration;
        private long _generation = Interlocked.Increment(ref s_nextGeneration);

        // Readers share the buffer's read lock, so they may replace this cache concurrently.
        // Publish the page and its coordinate together and capture that immutable pair once.
        // Writers still require the owning buffer's exclusive lock, including page reuse.
        internal sealed record PageAccess(TerminalPage Page, long StartAbsRow);
        private PageAccess? _lastAccessedPage;
        internal PageAccess? CachedPage => Volatile.Read(ref _lastAccessedPage);

        // ── Properties ───────────────────────────────────────────────────────────

        /// <summary>Number of rows currently accessible (after eviction).</summary>
        public int Count => (int)Math.Min(_totalRowsAppended - _totalRowsEvicted, int.MaxValue);

        /// <summary>Total rows that have ever been appended to this scrollback.</summary>
        public long TotalRowsAppended => _totalRowsAppended;

        /// <summary>Total rows that have been evicted from this scrollback due to budget limits.</summary>
        public long TotalRowsEvicted => _totalRowsEvicted;

        /// <summary>
        /// Monotonic id of the current absolute-row coordinate space. Stable across appends
        /// and evictions; changes whenever <see cref="Clear"/> resets the row counters (CSI 3J,
        /// RIS, user clear-buffer, reflow) and whenever a fresh store is constructed. A holder
        /// of an <c>AbsoluteRow</c> must record this alongside it and treat the row id as
        /// invalid once the two no longer match.
        /// </summary>
        public long Generation => Interlocked.Read(ref _generation);

        /// <summary>Approximate byte consumption of retained cell data.</summary>
        public long CurrentBytes => _currentBytes;

        // ── Construction ─────────────────────────────────────────────────────────

        /// <param name="cols">Number of columns per row (must match the buffer).</param>
        /// <param name="pool">Shared pool from which pages are rented and returned.</param>
        /// <param name="maxScrollbackBytes">Byte budget; defaults to 128 MB.</param>
        /// <param name="rowsPerPage">Rows per page slab; defaults to 64.</param>
        public ScrollbackPages(
            int cols,
            TerminalPagePool pool,
            long maxScrollbackBytes = 128L * 1024 * 1024,
            int rowsPerPage = TerminalPageConstants.DefaultRowsPerPage)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(cols);
            _cols = cols;
            _pool = pool ?? throw new ArgumentNullException(nameof(pool));
            _rowsPerPage = rowsPerPage;
            MaxScrollbackBytes = maxScrollbackBytes;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Appends a row to the scrollback. If the current page is full, a new page
        /// is rented from the pool. Budget eviction is applied afterwards.
        /// </summary>
        /// <param name="row">Read-only span of exactly <see cref="_cols"/> cells.</param>
        /// <param name="isWrapped">Whether this row is wrapped (flows into the next).</param>
        /// <param name="extendedText">Optional extended grapheme clusters keyed by column index.</param>
        /// <param name="hyperlinks">Optional hyperlink URIs keyed by column index.</param>
        /// <param name="underlines">Optional underline styles and colors keyed by column index (#270).</param>
        public long AppendRow(
            ReadOnlySpan<TerminalCell> row,
            bool isWrapped = false,
            SmallMap<string>? extendedText = null,
            SmallMap<Ntilde.VT.Links.Hyperlink>? hyperlinks = null,
            SmallMap<Ntilde.VT.CellUnderline>? underlines = null)
            => AppendRow(row, isWrapped, extendedText, hyperlinks, underlines, null);

        internal long AppendRow(
            ReadOnlySpan<TerminalCell> row,
            bool isWrapped,
            SmallMap<string>? extendedText,
            SmallMap<Ntilde.VT.Links.Hyperlink>? hyperlinks,
            SmallMap<Ntilde.VT.CellUnderline>? underlines,
            int? wrappedColumns, int? writtenColumns = null)
        {
            if (row.Length != _cols)
                throw new ArgumentException($"Row length {row.Length} must equal Cols {_cols}.", nameof(row));
            if (wrappedColumns is < 0 || wrappedColumns > _cols || (wrappedColumns.HasValue && !isWrapped))
                throw new ArgumentOutOfRangeException(nameof(wrappedColumns));

            if (writtenColumns is < 0 || writtenColumns > _cols)
                throw new ArgumentOutOfRangeException(nameof(writtenColumns));
            ValidateWrapTail(row, wrappedColumns, extendedText, hyperlinks, underlines);

            // Ensure there is a page with available space.
            if (_pages.Last == null || _pages.Last.Value.IsFull)
            {
                var page = _pool.Rent(_cols, _rowsPerPage);
                _pages.AddLast(page);
                _currentBytes += page.ByteSize;
            }

            var currentPage = _pages.Last!.Value;
            int rowIndex = currentPage.UsedRows;
            row.CopyTo(currentPage.GetRowSpan(rowIndex));
            currentPage.SetRowWrapped(rowIndex, isWrapped, wrappedColumns);
            currentPage.SetWrittenColumns(rowIndex, writtenColumns ?? wrappedColumns ?? _cols);
            if (extendedText != null && extendedText.Count > 0)
                currentPage.SetExtendedTextFromMap(rowIndex, extendedText);
            if (hyperlinks != null && hyperlinks.Count > 0)
                currentPage.SetHyperlinkFromMap(rowIndex, hyperlinks);
            if (underlines != null && underlines.Count > 0)
                currentPage.SetUnderlineFromMap(rowIndex, underlines);
            currentPage.UsedRows++;
            _totalRowsAppended++;

            TryEvictUntilWithinBudget();
            return _totalRowsAppended - 1; // absolute row index just appended
        }

        private static void ValidateWrapTail(ReadOnlySpan<TerminalCell> row, int? columns, SmallMap<string>? extended,
            SmallMap<Ntilde.VT.Links.Hyperlink>? links, SmallMap<Ntilde.VT.CellUnderline>? underlines)
        {
            if (columns is not int start) return;
            for (int col = start; col < row.Length; col++)
                if (!TerminalRow.IsWrapPaddingCell(row[col]) || extended?.TryGet(col, out _) == true
                    || links?.TryGet(col, out _) == true || underlines?.TryGet(col, out _) == true)
                    throw new ArgumentException("Wrap columns would hide cell content or metadata.", nameof(columns));
        }

        /// <summary>
        /// Retrieves a read-only span of cells for the given logical row index
        /// (0 = oldest retained row, Count-1 = newest row).
        /// </summary>
        public bool IsRowWrapped(int logicalIndex)
        {
            var (page, row) = FindPage(logicalIndex);
            return page.IsRowWrapped(row);
        }

        internal int GetWrittenColumns(int logicalIndex)
        {
            var (page, row) = FindPage(logicalIndex);
            return page.GetWrittenColumns(row);
        }

        public int? GetWrappedColumns(int logicalIndex)
        {
            var (page, row) = FindPage(logicalIndex);
            return page.GetWrappedColumns(row);
        }

        /// <summary>
        /// Retrieves a read-only span of cells for the given logical row index
        /// (0 = oldest retained row, Count-1 = newest row).
        /// </summary>
        public ReadOnlySpan<TerminalCell> GetRow(int logicalIndex)
            => GetRow(logicalIndex, CachedPage);

        // Accept the captured entry separately so an interleaving between capture and use
        // can be exercised deterministically without callbacks on the read hot path.
        // The caller must hold the owning read/write lock from capture through use.
        internal ReadOnlySpan<TerminalCell> GetRow(int logicalIndex, PageAccess? cached)
        {
            if ((uint)logicalIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(logicalIndex));

            long absRow = _totalRowsEvicted + logicalIndex;

            // Fast path: Sequential access cache
            if (cached != null && !cached.Page.IsReturned)
            {
                long pageEndAbs = cached.StartAbsRow + cached.Page.UsedRows;
                if (absRow >= cached.StartAbsRow && absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - cached.StartAbsRow);
                    return cached.Page.GetRowSpanReadOnly(rowInPage);
                }
            }

            long pageStartAbs = _totalRowsEvicted;

            foreach (var page in _pages)
            {
                long pageEndAbs = pageStartAbs + page.UsedRows;
                if (absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - pageStartAbs);
                    Volatile.Write(ref _lastAccessedPage, new PageAccess(page, pageStartAbs));
                    return page.GetRowSpanReadOnly(rowInPage);
                }
                pageStartAbs = pageEndAbs;
            }

            throw new InvalidOperationException($"Row {logicalIndex} not found in pages (absRow={absRow}).");
        }

        /// <summary>
        /// Copies the cells of a logical row into <paramref name="destination"/>.
        /// </summary>
        public void CopyRowTo(int logicalIndex, Span<TerminalCell> destination)
        {
            GetRow(logicalIndex).CopyTo(destination);
        }

        /// <summary>Returns the extended text SmallMap for the given logical row, or null if none.</summary>
        public SmallMap<string>? GetExtendedTextMap(int logicalIndex)
        {
            (TerminalPage page, int rowInPage) = FindPage(logicalIndex);
            return page.GetExtendedTextMap(rowInPage);
        }

        /// <summary>Returns the hyperlink SmallMap for the given logical row, or null if none.</summary>
        public SmallMap<Ntilde.VT.Links.Hyperlink>? GetHyperlinkMap(int logicalIndex)
        {
            (TerminalPage page, int rowInPage) = FindPage(logicalIndex);
            return page.GetHyperlinkMap(rowInPage);
        }

        /// <summary>Returns the underline SmallMap for the given logical row, or null if none.</summary>
        public SmallMap<Ntilde.VT.CellUnderline>? GetUnderlineMap(int logicalIndex)
        {
            (TerminalPage page, int rowInPage) = FindPage(logicalIndex);
            return page.GetUnderlineMap(rowInPage);
        }

        /// <summary>
        /// Helper to find the <see cref="TerminalPage"/> and row index within that page
        /// for a given logical row index.
        /// </summary>
        private (TerminalPage page, int rowInPage) FindPage(int logicalIndex)
        {
            if ((uint)logicalIndex >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(logicalIndex));

            long absRow = _totalRowsEvicted + logicalIndex;
            // Metadata readers share the same immutable page/coordinate cache as cell readers.
            // Capture once: another reader may publish a different page while this one runs.
            var cached = CachedPage;
            if (cached != null && !cached.Page.IsReturned)
            {
                long offset = absRow - cached.StartAbsRow;
                if (offset >= 0 && offset < cached.Page.UsedRows)
                    return (cached.Page, (int)offset);
            }
            long pageStartAbs = _totalRowsEvicted;

            foreach (var page in _pages)
            {
                long pageEndAbs = pageStartAbs + page.UsedRows;
                if (absRow < pageEndAbs)
                {
                    int rowInPage = (int)(absRow - pageStartAbs);
                    Volatile.Write(ref _lastAccessedPage, new PageAccess(page, pageStartAbs));
                    return (page, rowInPage);
                }
                pageStartAbs = pageEndAbs;
            }

            throw new InvalidOperationException($"Row {logicalIndex} not found in pages (absRow={absRow}).");
        }

        /// <summary>
        /// Attempts to remove the newest row from the scrollback (the reverse of AppendRow).
        /// Used during vertical resize (height grow) to pull history back into the viewport.
        /// </summary>
        public bool TryPopLastRow(Span<TerminalCell> destination)
            => TryPopLastRow(destination, out _, out _, out _, out _);

        /// <summary>
        /// As <see cref="TryPopLastRow(Span{TerminalCell})"/>, but also returns
        /// the row's wrap flag and side tables (extended text, hyperlinks, underlines) so a
        /// restored viewport row is complete — extended graphemes must survive
        /// the scrollback round trip. The vacated slot's metadata is cleared so
        /// a later append into it cannot resurrect stale entries.
        /// </summary>
        public bool TryPopLastRow(
            Span<TerminalCell> destination,
            out bool isWrapped,
            out SmallMap<string>? extendedText,
            out SmallMap<Ntilde.VT.Links.Hyperlink>? hyperlinks,
            out SmallMap<Ntilde.VT.CellUnderline>? underlines)
            => TryPopLastRow(destination, out isWrapped, out extendedText, out hyperlinks, out underlines, out _);

        public bool TryPopLastRow(
            Span<TerminalCell> destination,
            out bool isWrapped,
            out SmallMap<string>? extendedText,
            out SmallMap<Ntilde.VT.Links.Hyperlink>? hyperlinks,
            out SmallMap<Ntilde.VT.CellUnderline>? underlines,
            out int? wrappedColumns)
            => TryPopLastRow(destination, out isWrapped, out extendedText, out hyperlinks, out underlines, out wrappedColumns, out _);

        internal bool TryPopLastRow(Span<TerminalCell> destination, out bool isWrapped,
            out SmallMap<string>? extendedText, out SmallMap<Ntilde.VT.Links.Hyperlink>? hyperlinks,
            out SmallMap<Ntilde.VT.CellUnderline>? underlines, out int? wrappedColumns, out int writtenColumns)
        {
            writtenColumns = _cols;
            wrappedColumns = null;
            isWrapped = false;
            extendedText = null;
            hyperlinks = null;
            underlines = null;

            if (_pages.Last == null || _pages.Last.Value.UsedRows == 0)
                return false;

            var page = _pages.Last.Value;
            int rowIndex = page.UsedRows - 1;
            page.GetRowSpanReadOnly(rowIndex).CopyTo(destination);
            isWrapped = page.IsRowWrapped(rowIndex);
            wrappedColumns = page.GetWrappedColumns(rowIndex);
            writtenColumns = page.GetWrittenColumns(rowIndex);
            extendedText = page.GetExtendedTextMap(rowIndex);
            hyperlinks = page.GetHyperlinkMap(rowIndex);
            underlines = page.GetUnderlineMap(rowIndex);
            page.ClearRowMetadata(rowIndex);

            page.UsedRows--;
            _totalRowsAppended--;

            if (page.UsedRows == 0)
            {
                _pages.RemoveLast();
                _currentBytes -= page.ByteSize;
                InvalidateCachedPage(page);
                _pool.Return(page);
            }

            return true;
        }

        /// <summary>Rows held by the oldest page, or 0 when the store is empty.</summary>
        public int OldestPageRowCount => _pages.First?.Value.UsedRows ?? 0;

        /// <summary>
        /// Drops the oldest page and returns it to the pool, exactly as budget eviction does, and
        /// returns how many rows it held (0 when the store is empty). Logical indexes shift down by
        /// that many. For a caller that has finished reading those rows while rebuilding the store
        /// elsewhere - the reflow - so the rebuilt store reuses the page's array instead of
        /// allocating a second scrollback.
        /// </summary>
        public int ReleaseOldestPage()
        {
            if (_pages.First == null) return 0;

            var oldest = _pages.First.Value;
            _pages.RemoveFirst();
            int rows = oldest.UsedRows;
            _totalRowsEvicted += rows;
            _currentBytes -= oldest.ByteSize;
            InvalidateCachedPage(oldest);
            _pool.Return(oldest);
            return rows;
        }

        /// <summary>
        /// Evicts oldest pages until total bytes are within <see cref="MaxScrollbackBytes"/>.
        /// Returned pages go back to the pool.
        /// </summary>
        public void TryEvictUntilWithinBudget()
        {
            while (_currentBytes > MaxScrollbackBytes && _pages.Count > 0)
            {
                var oldest = _pages.First!.Value;
                _pages.RemoveFirst();
                _totalRowsEvicted += oldest.UsedRows;
                _currentBytes -= oldest.ByteSize;
                InvalidateCachedPage(oldest);
                _pool.Return(oldest);
            }
        }

        /// <summary>
        /// Removes all scrollback data and returns all pages to the pool.
        /// Resets the absolute-row counters, and therefore bumps <see cref="Generation"/>:
        /// every absolute row id issued before this call is now meaningless (it would
        /// resolve to a plausible-looking but wrong row rather than going negative).
        /// </summary>
        public void Clear()
        {
            foreach (var page in _pages)
                _pool.Return(page);

            _pages.Clear();
            _currentBytes = 0;
            _totalRowsAppended = 0;
            _totalRowsEvicted = 0;
            Interlocked.Exchange(ref _generation, Interlocked.Increment(ref s_nextGeneration));

            Volatile.Write(ref _lastAccessedPage, null);
        }

        private void InvalidateCachedPage(TerminalPage page)
        {
            // Called under the owning write lock: no reader can publish a new entry here.
            if (ReferenceEquals(CachedPage?.Page, page))
                Volatile.Write(ref _lastAccessedPage, null);
        }

        /// <summary>
        /// Updates the default foreground and background colors for all cells currently retained.
        /// This is allowed to be a slower full-scan because theme changes are infrequent.
        /// Explicit colors (including truecolors that happen to equal the old default) are left
        /// alone - only flagged default cells migrate.
        /// </summary>
        public void UpdateThemeDefaults(TerminalTheme oldTheme, TerminalTheme newTheme)
        {
            uint fgUint = newTheme.Foreground.ToUint();
            uint bgUint = newTheme.Background.ToUint();
            foreach (var page in _pages)
            {
                page.UpdateThemeDefaults(fgUint, bgUint);
            }
        }

        /// <summary>
        /// Returns a point-in-time snapshot of memory metrics for diagnostics.
        /// </summary>
        public ScrollbackMetrics GetMetrics() => new ScrollbackMetrics
        {
            RowCount = Count,
            ActivePages = _pool.ActivePages,
            PooledPages = _pool.PooledPages,
            BytesUsed = _currentBytes,
            MaxBytes = MaxScrollbackBytes,
        };
    }
}
