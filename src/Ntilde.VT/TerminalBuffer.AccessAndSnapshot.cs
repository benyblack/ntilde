using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Ntilde.VT;
using Ntilde.VT.Storage;

namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        public TerminalCell GetCell(int col, int fieldRow, int scrollOffset = 0)
        {
            // fieldRow is the visual row (0 to Rows-1)
            // We show: [scrollback tail] + [viewport]

            // The alternate screen has no scrollback to show: its absolute rows are its screen rows. Counted
            // from the main screen's scrollback, they read the wrong row, or past the end.
            if (_isAltScreen) return GetCellAbsolute(col, fieldRow);

            int totalLines = _scrollback.Count + Rows;
            // Visible Top Index = Total - Rows - Offset
            int displayStart = Math.Max(0, totalLines - Rows - scrollOffset);

            int actualIndex = displayStart + fieldRow;

            return GetCellAbsolute(col, actualIndex);
        }

        /// <summary>
        /// Returns the <see cref="TerminalRow"/> at an absolute row index (scrollback + viewport),
        /// or <c>null</c> when no persistent row object exists at that index. Requires the read
        /// lock (see <see cref="AssertLockHeld"/>).
        /// </summary>
        /// <remarks>
        /// Returns <c>null</c> for: negative indices, out-of-range viewport rows, and — importantly —
        /// any <b>scrollback</b> row. Scrollback is stored paged in <c>ScrollbackPages</c>, not as
        /// <see cref="TerminalRow"/> objects, so it has no row to hand back. To read scrollback
        /// content use <see cref="GetCellAbsolute"/> / <see cref="GetGraphemeAbsolute"/>, which page
        /// it in. Callers that assume a non-null row for scrollback indices will dereference null.
        /// </remarks>
        public TerminalRow? GetRowAbsolute(int absRow)
        {
            AssertLockHeld();
            if (absRow < 0) return null;

            if (_isAltScreen)
            {
                if (absRow >= Rows) return null;
                return _viewport[absRow];
            }

            // Scrollback rows are now paged, they don't exist as persistent TerminalRow objects.
            if (absRow < _scrollback.Count) return null;

            int viewportRow = absRow - _scrollback.Count;
            if (viewportRow < 0 || viewportRow >= Rows) return null;
            return _viewport[viewportRow];
        }

        /// <summary>
        /// True when the row at an absolute index ends because it auto-wrapped rather than
        /// because of an explicit newline — i.e. the next row continues the same logical line.
        /// Requires the read lock.
        /// </summary>
        /// <remarks>
        /// Unlike <see cref="GetRowAbsolute"/> this answers for scrollback rows too, reading the
        /// per-row wrap bit the pages carry. Out-of-range indices answer <c>false</c> rather
        /// than throwing, so callers walking a span can stop at the buffer edge naturally.
        /// </remarks>
        public bool IsRowWrappedAbsolute(int absRow)
        {
            AssertLockHeld();
            if (absRow < 0) return false;

            if (_isAltScreen)
            {
                return absRow < Rows && _viewport[absRow].IsWrapped;
            }

            if (absRow < _scrollback.Count)
            {
                return _scrollback.IsRowWrapped(absRow);
            }

            int viewportRow = absRow - _scrollback.Count;
            return viewportRow >= 0 && viewportRow < Rows && _viewport[viewportRow].IsWrapped;
        }

        public TerminalCell GetCellAbsolute(int col, int absRow)
        {
            AssertLockHeld();
            if (absRow < 0 || col < 0 || col >= Cols) return TerminalCell.Default;

            if (_isAltScreen)
            {
                if (absRow >= Rows) return TerminalCell.Default;
                return _viewport[absRow].Cells[col];
            }

            if (absRow < _scrollback.Count)
            {
                // Reading from paged scrollback
                return _scrollback.GetRow(absRow)[col];
            }
            else
            {
                // Reading from viewport
                int viewportRow = absRow - _scrollback.Count;
                if (viewportRow < 0 || viewportRow >= Rows) return TerminalCell.Default;
                return _viewport[viewportRow].Cells[col];
            }
        }

        public string GetGraphemeAbsolute(int col, int absRow)
        {
            AssertLockHeld();
            if (absRow < 0 || col < 0 || col >= Cols) return " ";

            if (!_isAltScreen && absRow < _scrollback.Count)
            {
                // The side table has been carried into scrollback since the row-metadata
                // work landed; this read path was the missing half (#95 gap 1). Without it
                // an emoji or CJK grapheme degraded to its first UTF-16 char the moment the
                // line scrolled off - visible in copied text and in link detection, both of
                // which read through here.
                var extendedText = _scrollback.GetExtendedTextMap(absRow);
                if (extendedText != null && extendedText.TryGet(col, out string? scrollbackText)
                    && scrollbackText != null)
                {
                    return scrollbackText;
                }

                return _scrollback.GetRow(absRow)[col].Character.ToString();
            }

            var row = GetRowAbsolute(absRow);
            if (row == null) return " ";
            var cell = row.Cells[col];
            return (cell.HasExtendedText ? row.GetExtendedText(col) : null) ?? cell.Character.ToString();
        }

        /// <summary>
        /// Returns the URI of the hyperlink on a cell, or <c>null</c> if it has none.
        /// </summary>
        /// <remarks>
        /// For "do these two cells belong to the same anchor?" use
        /// <see cref="GetHyperlinkIdentityAbsolute"/> instead — equal URIs do not imply one link, which is
        /// the whole reason OSC 8 has an <c>id</c> parameter.
        /// </remarks>
        public string? GetHyperlinkAbsolute(int col, int absRow)
            => GetHyperlinkIdentityAbsolute(col, absRow)?.Uri;

        /// <summary>
        /// Returns the hyperlink identity on a cell, or <c>null</c> if it has none. Cells in the same
        /// logical OSC 8 anchor return the same instance, so identity is reference equality.
        /// </summary>
        public Links.Hyperlink? GetHyperlinkIdentityAbsolute(int col, int absRow)
        {
            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                if (col < 0 || col >= Cols) return null;

                return HyperlinkMapAbsolute(absRow) is { } hyperlinks && hyperlinks.TryGet(col, out Links.Hyperlink? link)
                    ? link
                    : null;
            }
            finally { ExitReadLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>
        /// Every run of cells carrying <paramref name="link"/> on rows <paramref name="firstAbsRow"/> to
        /// <paramref name="lastAbsRow"/>, in reading order: its part on the row it wrapped onto, and each run an
        /// <c>id</c> joined to it, as well as the run itself (#95 gap 3). By identity, so a neighbouring link to the
        /// same URI is not part of it. One read lock for the whole range, and a row with no links costs a lookup.
        /// </summary>
        public IReadOnlyList<Links.HyperlinkRun> FindHyperlinkRuns(Links.Hyperlink link, int firstAbsRow, int lastAbsRow)
        {
            ArgumentNullException.ThrowIfNull(link);
            var runs = new List<Links.HyperlinkRun>();
            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                for (int absRow = Math.Max(0, firstAbsRow); absRow <= lastAbsRow; absRow++)
                {
                    if (HyperlinkMapAbsolute(absRow) is not { Count: > 0 } hyperlinks) continue;

                    int start = -1;
                    for (int col = 0; col <= Cols; col++)
                    {
                        bool inLink = col < Cols && hyperlinks.TryGet(col, out Links.Hyperlink? cell) && ReferenceEquals(cell, link);
                        if (inLink && start < 0)
                        {
                            start = col;
                        }
                        else if (!inLink && start >= 0)
                        {
                            runs.Add(new Links.HyperlinkRun(absRow, start, col - 1));
                            start = -1;
                        }
                    }
                }

                return runs;
            }
            finally { ExitReadLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>
        /// The underline style and color of a cell (#270), wherever its row lives, or <c>null</c> when it has none:
        /// not underlined, or a straight underline in the text color.
        /// </summary>
        public CellUnderline? GetUnderlineAbsolute(int col, int absRow)
        {
            bool lockTaken = EnterReadLockIfNeeded();
            try
            {
                if (col < 0 || col >= Cols || absRow < 0) return null;

                Storage.SmallMap<CellUnderline>? underlines = !_isAltScreen && absRow < _scrollback.Count
                    ? _scrollback.GetUnderlineMap(absRow)
                    : GetRowAbsolute(absRow)?.GetUnderlineMap();
                return underlines is not null && underlines.TryGet(col, out CellUnderline? underline) ? underline : null;
            }
            finally { ExitReadLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>The hyperlink side table of an absolute row, wherever the row lives; null when it has none. Lock held.</summary>
        private Storage.SmallMap<Links.Hyperlink>? HyperlinkMapAbsolute(int absRow)
        {
            if (absRow < 0) return null;

            // Same gap as GetGraphemeAbsolute: the hyperlink side table survives into scrollback, but this read
            // path returned null unconditionally, so an OSC 8 link stopped being a link as soon as it scrolled
            // off screen.
            return !_isAltScreen && absRow < _scrollback.Count
                ? _scrollback.GetHyperlinkMap(absRow)
                : GetRowAbsolute(absRow)?.GetHyperlinkMap();
        }

        public string GetGrapheme(int col, int viewRow)
        {
            AssertLockHeld();
            if (viewRow < 0 || viewRow >= Rows) return " ";
            var row = _viewport[viewRow];
            if (col < 0 || col >= Cols) return " ";
            var cell = row.Cells[col];
            return (cell.HasExtendedText ? row.GetExtendedText(col) : null) ?? cell.Character.ToString();
        }

        public int GetVisualCursorRow(int scrollOffset = 0)
        {
            // Cursor is at _scrollback.Count + CursorRow
            // Visible Start is _scrollback.Count + Rows - Rows - scrollOffset

            // Logical Index of Cursor:
            // int cursorAbsIndex = _scrollback.Count + CursorRow;
            // Logical Index of Screen Top:
            // int screenTopAbsIndex = (_scrollback.Count) - scrollOffset; 

            // Visual Row = CursorAbs - ScreenTop
            // = (_scrollback.Count + CursorRow) - (_scrollback.Count - scrollOffset)
            // = CursorRow + scrollOffset

            return CursorRow + scrollOffset;
        }

        private void CaptureCursorStateNoLock(CursorState target)
        {
            target.Row = _cursorRow;
            target.Col = _cursorCol;
            target.Foreground = CurrentForeground;
            target.Background = CurrentBackground;
            target.FgIndex = CurrentFgIndex;
            target.BgIndex = CurrentBgIndex;
            target.IsDefaultForeground = IsDefaultForeground;
            target.IsDefaultBackground = IsDefaultBackground;
            target.IsInverse = IsInverse;
            target.IsBold = IsBold;
            target.IsFaint = IsFaint;
            target.IsItalic = IsItalic;
            target.IsUnderline = IsUnderline;
            target.UnderlineStyle = UnderlineStyle;
            target.UnderlineColor = UnderlineColor;
            target.UnderlineColorIndex = UnderlineColorIndex;
            target.IsBlink = IsBlink;
            target.IsStrikethrough = IsStrikethrough;
            target.IsHidden = IsHidden;
            target.IsPendingWrap = _isPendingWrap;
        }

        private void ApplyCursorStateNoLock(CursorState source)
        {
            _cursorRow = Math.Clamp(source.Row, 0, Rows - 1);
            _cursorCol = Math.Clamp(source.Col, 0, Cols - 1);
            CurrentForeground = source.Foreground;
            CurrentBackground = source.Background;
            CurrentFgIndex = source.FgIndex;
            CurrentBgIndex = source.BgIndex;
            IsDefaultForeground = source.IsDefaultForeground;
            IsDefaultBackground = source.IsDefaultBackground;
            IsInverse = source.IsInverse;
            IsBold = source.IsBold;
            IsFaint = source.IsFaint;
            IsItalic = source.IsItalic;
            IsUnderline = source.IsUnderline;
            UnderlineStyle = source.UnderlineStyle;
            UnderlineColor = source.UnderlineColor;
            UnderlineColorIndex = source.UnderlineColorIndex;
            IsBlink = source.IsBlink;
            IsStrikethrough = source.IsStrikethrough;
            IsHidden = source.IsHidden;
            _isPendingWrap = source.IsPendingWrap;
        }

        private void SaveActiveScreenStateNoLock()
        {
            var target = _isAltScreen ? _screenCursorStates.Alt : _screenCursorStates.Main;
            CaptureCursorStateNoLock(target);
        }

        private void RestoreScreenStateNoLock(bool altScreen)
        {
            var source = altScreen ? _screenCursorStates.Alt : _screenCursorStates.Main;
            ApplyCursorStateNoLock(source);
        }

        private void ClearAltScreenNoLock()
        {
            _textSizingPruneRequired = true;
            for (int i = 0; i < Rows; i++)
            {
                var row = _viewport[i];
                row.IsWrapped = false;
                row.WrittenColumns = 0;
                for (int c = 0; c < Cols; c++)
                {
                    row.Cells[c] = TerminalCell.Default;
                }

                row.ClearExtendedText();
                row.ClearHyperlinks();
                row.ClearUnderlines();
                row.ClearTextSizing();
                row.TouchRevision();
            }
        }

        private void ResetCursorStateToDefaultsNoLock()
        {
            _cursorRow = 0;
            _cursorCol = 0;
            _isPendingWrap = false;
            CurrentForeground = Theme.Foreground;
            CurrentBackground = Theme.Background;
            CurrentFgIndex = -1;
            CurrentBgIndex = -1;
            IsDefaultForeground = true;
            IsDefaultBackground = true;
            IsInverse = false;
            IsBold = false;
            IsFaint = false;
            IsItalic = false;
            IsUnderline = false;
            UnderlineStyle = UnderlineStyle.Single;
            UnderlineColor = null;
            UnderlineColorIndex = -1;
            IsBlink = false;
            IsStrikethrough = false;
            IsHidden = false;
        }

        /// <summary>
        /// Reinitializes a saved cursor/style slot (DECSC bank or per-screen state) to the
        /// terminal's initial state. RIS has to do this to every slot: leaving one populated
        /// lets a later DECRC or alt-screen exit hand pre-RIS style back, which also re-pins
        /// the live state to explicit colors that no longer follow theme switches.
        /// </summary>
        private void ResetSavedCursorStateToDefaultsNoLock(CursorState state)
        {
            state.Row = 0;
            state.Col = 0;
            state.Foreground = Theme.Foreground;
            state.Background = Theme.Background;
            state.FgIndex = -1;
            state.BgIndex = -1;
            state.IsDefaultForeground = true;
            state.IsDefaultBackground = true;
            state.IsInverse = false;
            state.IsBold = false;
            state.IsFaint = false;
            state.IsItalic = false;
            state.IsUnderline = false;
            state.UnderlineStyle = UnderlineStyle.Single;
            state.UnderlineColor = null;
            state.UnderlineColorIndex = -1;
            state.IsBlink = false;
            state.IsStrikethrough = false;
            state.IsHidden = false;
            state.IsPendingWrap = false;
            state.OriginMode = false;
            state.Charsets = [0, 0, 0, 0];
            state.Gl = 0;
        }

        private void SyncThemeDefaultsInCursorStateNoLock(CursorState state)
        {
            if (state.IsDefaultForeground)
            {
                state.Foreground = Theme.Foreground;
            }

            if (state.IsDefaultBackground)
            {
                state.Background = Theme.Background;
            }
        }

        private TerminalRow[] ResizeDetachedScreenBufferNoLock(TerminalRow[] source)
        {
            var resized = new TerminalRow[Rows];
            for (int i = 0; i < Rows; i++)
            {
                resized[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);

                if (i >= source.Length)
                {
                    continue;
                }

                int copyCols = Math.Min(Cols, source[i].Cells.Length);
                for (int c = 0; c < copyCols; c++)
                {
                    resized[i].Cells[c] = source[i].Cells[c];
                }

                resized[i].IsWrapped = source[i].IsWrapped;
                resized[i].WrittenColumns = Math.Min(source[i].WrittenColumns, Cols);
                resized[i].WrappedColumns = source[i].IsWrapped
                    ? Math.Min(source[i].WrappedColumns ?? source[i].Cells.Length, Cols) : null;
                // Detached buffers are not reflowed either, so the side tables transfer by column
                // (#164). Copying Cells and IsWrapped alone lost extended graphemes and links.
                resized[i].CopyRowMetadataFrom(source[i], copyCols);
            }

            return resized;
        }
        // Parser-only access while its batch lock is held; no mutable slot escapes a host API.
        internal CursorState ActiveSavedCursor
        {
            get { AssertLockHeld(); return _isAltScreen ? _savedCursors.Alt : _savedCursors.Main; }
        }
        internal bool RestoresMainCursorOnAltExit => _restoreMainCursorOnAltExit;

        private void ApplySavedCursorNoLock(CursorState source)
        {
            ApplyCursorStateNoLock(source);
            if (source.OriginMode is bool origin) Modes.IsOriginMode = origin;
            // DECSC restores pending wrap, not DECAWM; the current autowrap mode survives.
        }

        // Saved Cursor State (DEC SC / DEC RC)
        public void SaveCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                var target = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
                CaptureCursorStateNoLock(target);
                target.OriginMode = Modes.IsOriginMode;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void RestoreCursor()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                var source = _isAltScreen ? _savedCursors.Alt : _savedCursors.Main;
                ApplySavedCursorNoLock(source);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        public void EraseLineToEnd()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                EraseTextSizingRange(_cursorCol, Cols, TextSizingAbsoluteRow(_cursorRow), TextSizingAbsoluteRow(_cursorRow) + 1);
                var row = _viewport[_cursorRow];
                if (_cursorCol >= 0 && _cursorCol < Cols) row.NoteColumnsWritten(Cols);
                for (int i = _cursorCol; i < Cols; i++)
                {
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                    row.SetHyperlink(i, null);
                    row.SetUnderline(i, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineFromStart()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                EraseTextSizingRange(0, Math.Min(Cols, _cursorCol + 1), TextSizingAbsoluteRow(_cursorRow), TextSizingAbsoluteRow(_cursorRow) + 1);
                var row = _viewport[_cursorRow];
                row.NoteColumnsWritten(Math.Min(Cols, _cursorCol + 1));
                for (int i = 0; i <= _cursorCol; i++)
                {
                    if (i >= Cols) break;
                    row.Cells[i] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(i, null);
                    row.SetHyperlink(i, null);
                    row.SetUnderline(i, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                ClearRowInternal(_cursorRow);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            OnInvalidate?.Invoke();
        }

        public void EraseLineAll(int rowIndex)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (rowIndex < 0 || rowIndex >= Rows) return;
                ClearRowInternal(rowIndex);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            OnInvalidate?.Invoke();
        }

        private void ClearRowInternal(int rowIndex)
        {
            EraseTextSizingRange(0, Cols, TextSizingAbsoluteRow(rowIndex), TextSizingAbsoluteRow(rowIndex) + 1);
            var row = _viewport[rowIndex];
            row.WrappedColumns = null;
            row.WrittenColumns = 0;
            var empty = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
            for (int i = 0; i < Cols; i++)
            {
                row.Cells[i] = empty;
            }
            row.ClearExtendedText();
            row.ClearHyperlinks();
            row.ClearUnderlines();
            row.ClearTextSizing();
            row.TouchRevision();
        }

        public void EraseCharacters(int count)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < 0 || _cursorRow >= Rows) return;
                EraseTextSizingRange(_cursorCol, (int)Math.Min(Cols, (long)_cursorCol + Math.Max(0, count)), TextSizingAbsoluteRow(_cursorRow), TextSizingAbsoluteRow(_cursorRow) + 1);
                var row = _viewport[_cursorRow];
                if (count > 0 && _cursorCol >= 0 && _cursorCol < Cols)
                    row.NoteColumnsWritten((int)Math.Min(Cols, (long)_cursorCol + count));

                for (int i = 0; i < count; i++)
                {
                    int col = _cursorCol + i;
                    if (col >= Cols) break;

                    row.Cells[col] = new TerminalCell(' ', CurrentForeground, CurrentBackground, false, false, IsDefaultForeground, IsDefaultBackground);
                    row.SetExtendedText(col, null);
                    row.SetHyperlink(col, null);
                    row.SetUnderline(col, null);
                }
                row.TouchRevision();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void InsertLines(int count)
        {
            TerminalLogger.Log($"[InsertLines] Count:{count} CursorRow:{_cursorRow}");
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < ScrollTop || _cursorRow > ScrollBottom) return;

                int top = _cursorRow;
                int bottom = ScrollBottom;
                // Clip count to available space
                int n = Math.Min(count, bottom - top + 1);

                if (n <= 0) return;

                int sizedTop = TextSizingAbsoluteRow(top);
                int sizedEnd = TextSizingAbsoluteRow(bottom + 1);
                EraseTextSizingAcrossBoundary(sizedTop);
                EraseTextSizingAcrossBoundary(sizedEnd);
                EraseTextSizingRange(0, Cols, sizedEnd - n, sizedEnd);

                // Shift lines DOWN
                // To insert N lines at TOP, we must move lines starting at TOP down by N.
                // We iterate backwards from (bottom - n) to top to avoid overwriting.
                for (int i = bottom - n; i >= top; i--)
                {
                    _viewport[i + n] = _viewport[i];
                }

                // Update images: Shift images starting at top DOWN by n
                int absTop = _isAltScreen ? top : (_scrollback.Count + top);
                int absBottom = _isAltScreen ? bottom : (_scrollback.Count + bottom);
                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    // Only the active screen's images move (see ScrollUpInternal).
                    if (img.IsAltScreenImage != _isAltScreen) continue;

                    // If image starts in or below the insertion point, shift it down
                    if (img.IsSticky && img.CellY >= absTop && img.CellY <= absBottom)
                    {
                        img.CellY += n;
                        if (ClipSixelImage(img, absTop, absBottom) || img.CellY > absBottom)
                        {
                            RetireImage(img);
                            _images.RemoveAt(i);
                        }
                    }
                }

                // Fill the gap created at TOP with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[top + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }

                // Moved rows keep their revisions, as in ScrollUpInternal (#657).
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void DeleteLines(int count)
        {
            TerminalLogger.Log($"[DeleteLines] Count:{count} CursorRow:{_cursorRow}");
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_cursorRow < ScrollTop || _cursorRow > ScrollBottom) return;

                int top = _cursorRow;
                int bottom = ScrollBottom;
                // Clip count to available space
                int n = Math.Min(count, bottom - top + 1);

                if (n <= 0) return;

                int sizedTop = TextSizingAbsoluteRow(top);
                int sizedEnd = TextSizingAbsoluteRow(bottom + 1);
                EraseTextSizingRange(0, Cols, sizedTop, sizedTop + n);
                EraseTextSizingAcrossBoundary(sizedEnd);

                // Shift lines UP
                // To delete N lines at TOP, we shift content from (top + n) UP to top.
                // We iterate forwards.
                for (int i = top; i <= bottom - n; i++)
                {
                    _viewport[i] = _viewport[i + n];
                }

                // Update images: Shift images below the deleted range UP by n
                int absTop = _isAltScreen ? top : (_scrollback.Count + top);
                int absBottom = _isAltScreen ? bottom : (_scrollback.Count + bottom);
                int absDeletedEnd = absTop + n;

                for (int i = _images.Count - 1; i >= 0; i--)
                {
                    var img = _images[i];
                    // Only the active screen's images move (see ScrollUpInternal).
                    if (img.IsAltScreenImage != _isAltScreen) continue;

                    // If image overlaps with or is below the deleted range
                    if (img.IsSticky && img.CellY + img.CellHeight > absTop && img.CellY <= absBottom)
                    {
                        if (img.CellY < absDeletedEnd)
                        {
                            // Image starts in the deleted range
                            RetireImage(img);
                            _images.RemoveAt(i);
                        }
                        else
                        {
                            // Image is below the deleted range, shift it UP
                            img.CellY -= n;
                            if (ClipSixelImage(img, absTop, absBottom))
                            {
                                RetireImage(img);
                                _images.RemoveAt(i);
                            }
                        }
                    }
                }

                // Fill the gap created at BOTTOM with new blank lines
                for (int i = 0; i < n; i++)
                {
                    _viewport[bottom - n + 1 + i] = new TerminalRow(Cols, CurrentForeground, CurrentBackground);
                }

                // Moved rows keep their revisions, as in ScrollUpInternal (#657).
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            OnInvalidate?.Invoke();
        }

        public void SetCursorPosition(int col, int row)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _cursorCol = Math.Clamp(col, 0, Cols - 1);
                _cursorRow = Math.Clamp(row, 0, Rows - 1);
                _isPendingWrap = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
        }

        /// <summary>
        /// Switch to alternate screen buffer (used by vim, htop, less, etc.)
        /// </summary>
        public void EnterAltScreen(bool clearAlt, bool saveCursorForExit)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (_isAltScreen) return;

                SaveActiveScreenStateNoLock();

                if (saveCursorForExit)
                {
                    CaptureCursorStateNoLock(_savedCursors.Main);
                    _savedCursors.Main.OriginMode = Modes.IsOriginMode;
                    _restoreMainCursorOnAltExit = true;
                }

                _mainScreen = _viewport;  // Save current viewport as main screen

                // Reset scrolling region to full screen when switching screens
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

                // CRITICAL FIX: Ensure Alt Screen buffer matches current dimensions
                // If we resized while in Main Screen, _altScreen might be stale (wrong size).
                if (_altScreen == null || _altScreen.Length != Rows || (_altScreen.Length > 0 && _altScreen[0].Cells.Length != Cols))
                {
                    _altScreen = new TerminalRow[Rows];
                    for (int i = 0; i < Rows; i++)
                    {
                        _altScreen[i] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
                    }
                }

                _isAltScreen = true;

                // The prompt that emitted 133;B is not on the screen the user is looking at any
                // more, so the command-input window closes on the way in. It does NOT reopen on the
                // way out: the shell repaints its prompt when the alt screen is torn down and that
                // repaint re-emits B, so the gate reopens on evidence rather than on assumption.
                lock (_trackedMarkGate)
                {
                    _isAcceptingCommandInput = false;
                }

                _viewport = _altScreen;    // Switch to alt screen

                // Kitty keyboard protocol: main and alternate screens own independent
                // progressive-enhancement flag stacks, so entering the alt screen swaps
                // which stack the key encoder consults.
                Modes.KittyKeyboard.SetActiveScreen(true);

                if (clearAlt)
                {
                    ClearAltScreenNoLock();
                    ResetCursorStateToDefaultsNoLock();
                    CaptureCursorStateNoLock(_screenCursorStates.Alt);
                }
                else
                {
                    RestoreScreenStateNoLock(altScreen: true);
                }

                // Reset row-diff cache so the next CaptureRenderSnapshot compares the alt-screen
                // rows fresh rather than against stale main-screen row IDs.
                _hasSnapshotState = false;
                Invalidate();
                OnScreenSwitched?.Invoke(true); // Notify that we switched to alt screen
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        public void SwitchToAltScreen()
        {
            EnterAltScreen(clearAlt: true, saveCursorForExit: false);
        }

        /// <summary>
        /// Switch back to main screen buffer
        /// </summary>
        public void SwitchToMainScreen(bool restoreSavedCursorIfArmed = true)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                if (!_isAltScreen) return;

                SaveActiveScreenStateNoLock();
                _altScreen = _viewport;    // Save current viewport as alt screen
                _isAltScreen = false;

                // Kitty keyboard protocol: swap back to the main screen's flag stack.
                Modes.KittyKeyboard.SetActiveScreen(false);

                if (_mainScreen.Length != Rows || (_mainScreen.Length > 0 && _mainScreen[0].Cells.Length != Cols))
                {
                    _mainScreen = ResizeDetachedScreenBufferNoLock(_mainScreen);
                }

                _viewport = _mainScreen;   // Restore main screen

                // Reset scrolling region to full screen when switching back to main screen
                ScrollTop = 0;
                ScrollBottom = Rows - 1;

                RestoreScreenStateNoLock(altScreen: false);
                if (restoreSavedCursorIfArmed && _restoreMainCursorOnAltExit)
                {
                    ApplySavedCursorNoLock(_savedCursors.Main);
                }

                _restoreMainCursorOnAltExit = false;

                // Reset row-diff cache so the next CaptureRenderSnapshot compares the main-screen
                // rows fresh rather than against stale alt-screen row IDs.
                _hasSnapshotState = false;
                Invalidate();
                OnScreenSwitched?.Invoke(false); // Notify that we switched back to main screen
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }

        /// <summary>
        /// Set scrolling region (DECSTBM) for vim splits, tmux, etc.
        /// </summary>
        public void SetScrollingRegion(int top, int bottom)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int newTop = Math.Clamp(top, 0, Rows - 1);
                int newBottom = Math.Clamp(bottom, 0, Rows - 1);

                // DECSTBM requires a region spanning at least two lines.
                // Ignore invalid updates instead of silently resetting to full screen.
                if (newTop >= newBottom)
                {
                    return;
                }

                ScrollTop = newTop;
                ScrollBottom = newBottom;
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
        }
        private static int SearchCellEnd(ReadOnlySpan<TerminalCell> cells, int column) =>
            column + 1 < cells.Length && cells[column + 1].IsWideContinuation ? column + 1 : column;

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1822:Mark members as static",
            Justification = "Part of the buffer's API: the reflow engine and the width tests ask the buffer that lays the text out. It delegates to UnicodeWidth today.")]
        public int GetGraphemeWidth(ReadOnlySpan<char> textElement)
        {
            return UnicodeWidth.GetGraphemeWidth(textElement);
        }

        public RenderRowSnapshot GetRowSnapshot(int absRow, int bufferCols)
        {
            AssertLockHeld();
            // This MUST be called under the buffer read/write lock.
            var row = GetRowAbsolute(absRow);
            if (row == null)
            {
                return new RenderRowSnapshot
                {
                    AbsRow = absRow,
                    Revision = 0,
                    Cols = bufferCols,
                    Cells = System.Array.Empty<RenderCellSnapshot>()
                };
            }

            var snapshot = new RenderRowSnapshot
            {
                AbsRow = absRow,
                Revision = row.Revision,
                Cols = bufferCols,
                Cells = new RenderCellSnapshot[bufferCols],
                RowId = row.Id
            };

            PopulateRenderCellsFromRow_NoLock(row, bufferCols, snapshot.Cells);

            return snapshot;
        }

        private static void PopulateRenderCellsFromRow_NoLock(TerminalRow row, int bufferCols, RenderCellSnapshot[] destination)
        {
            int colsToWrite = Math.Min(bufferCols, destination.Length);
            for (int c = 0; c < colsToWrite; c++)
            {
                var cell = (c < row.Cells.Length) ? row.Cells[c] : TerminalCell.Default;
                destination[c] = new RenderCellSnapshot
                {
                    Character = cell.Character,
                    Text = cell.HasExtendedText ? row.GetExtendedText(c) : null,
                    TextSizing = row.GetTextSizing(c),
                    Foreground = cell.Foreground,
                    Background = cell.Background,
                    IsInverse = cell.IsInverse,
                    IsBold = cell.IsBold,
                    IsDefaultForeground = cell.IsDefaultForeground,
                    IsDefaultBackground = cell.IsDefaultBackground,
                    IsWide = cell.IsWide,
                    IsWideContinuation = cell.IsWideContinuation,
                    IsHidden = cell.IsHidden,
                    IsFaint = cell.IsFaint,
                    IsItalic = cell.IsItalic,
                    IsUnderline = cell.IsUnderline,
                    Underline = cell.IsUnderline ? row.GetUnderline(c) : null,
                    IsBlink = cell.IsBlink,
                    IsStrikethrough = cell.IsStrikethrough,
                    FgIndex = cell.FgIndex,
                    BgIndex = cell.BgIndex
                };
            }
        }

        public List<RenderImageSnapshot> GetVisibleImagesSnapshot(int absDisplayStart, int bufferRows)
        {
            AssertLockHeld();
            // This MUST be called under the buffer read/write lock.
            var list = new List<RenderImageSnapshot>();
            int absEnd = absDisplayStart + bufferRows;

            foreach (var img in _images)
            {
                // Only images belonging to the active screen are visible. Main-screen
                // images use absolute CellY, alt-screen images viewport-relative; without
                // this filter an inactive-screen image whose (re-based) CellY happens to
                // overlap the visible range would bleed through (e.g. after ED 3 rebases
                // main-screen anchors while the alt screen is active).
                if (img.IsAltScreenImage != _isAltScreen) continue;

                // Simple visibility check
                if (img.CellY + img.CellHeight - img.ClipBottomRows > absDisplayStart && img.CellY + img.ClipTopRows < absEnd)
                {
                    list.Add(new RenderImageSnapshot
                    {
                        CellX = img.CellX,
                        CellY = img.CellY,
                        CellWidth = img.CellWidth,
                        CellHeight = img.CellHeight,
                        ImageHandle = img.ImageHandle,
                        Source = img.SixelSource ?? (img.ImageHandle as IDeferredSixelImage)?.Source,
                        ClipRightColumns = img.ClipRightColumns,
                        ClipTopRows = img.ClipTopRows,
                        ClipBottomRows = img.ClipBottomRows,
                        IsSticky = img.IsSticky
                    });
                }
            }
            return list;
        }

        /// <summary>Rejects malformed disk payloads before resize or snapshot application mutates state.</summary>
        public static void ValidateReplaySnapshot(ReplaySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Cols <= 0 || snapshot.Rows <= 0)
                throw new InvalidOperationException("Invalid replay geometry: columns and rows must be positive.");
            byte[]? cells = string.IsNullOrEmpty(snapshot.CellsBase64) ? null : Convert.FromBase64String(snapshot.CellsBase64);
            ValidateWrittenColumns(snapshot.RowWrittenColumns, snapshot.Rows, snapshot.Cols, "legacy replay");
            ValidateReplayTextSizing(snapshot, cells);
            if (snapshot.RowWrapColumns is { } widths)
            {
                ValidateWrappedColumns(new TerminalScreenState
                {
                    RowWraps = snapshot.RowWraps,
                    RowWrapColumns = widths,
                    RowWrittenColumns = snapshot.RowWrittenColumns,
                    ExtendedText = snapshot.ExtendedText
                }, snapshot.Rows, snapshot.Cols, cells, "legacy replay");
                if (snapshot.Underlines != null)
                    foreach (int key in snapshot.Underlines.Keys)
                    {
                        int row = key / snapshot.Cols, col = key % snapshot.Cols;
                        if (row >= 0 && row < widths.Length && widths[row] >= 0 && col >= widths[row])
                            throw StateTransferValidation.Reject("legacy replay", "wrap padding carries underline metadata");
                    }
            }
        }

        public void ApplySnapshot(ReplaySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.State is { Version: TerminalStateSnapshot.CurrentVersion })
            {
                ImportState(snapshot.State);
                return;
            }
            ValidateReplaySnapshot(snapshot);
            byte[]? legacyCells = string.IsNullOrEmpty(snapshot.CellsBase64) ? null : Convert.FromBase64String(snapshot.CellsBase64);
            AcquireWriteLock();
            try
            {
                if (legacyCells != null && System.Runtime.InteropServices.MemoryMarshal.Cast<byte, TerminalCell>(legacyCells.AsSpan()).Length
                    >= (long)snapshot.Cols * snapshot.Rows)
                    RetireLegacyViewportTextSizing(Math.Min(snapshot.Cols, Cols), Math.Min(snapshot.Rows, Rows));

                // Legacy files cannot reconstruct a save from earlier chunks. Use deterministic defaults
                // instead of retaining banks from a previous seek on this replay window.
                ResetSavedCursorStateToDefaultsNoLock(_savedCursors.Main);
                ResetSavedCursorStateToDefaultsNoLock(_savedCursors.Alt);
                _restoreMainCursorOnAltExit = false;
                ResetCursorPresentationNoLock();

                // Core properties
                _cursorCol = Math.Clamp(snapshot.CursorCol, 0, Math.Max(0, Cols - 1));
                _cursorRow = Math.Clamp(snapshot.CursorRow, 0, Math.Max(0, Rows - 1));
                _isAltScreen = snapshot.IsAltScreen;
                ScrollTop = Math.Clamp(snapshot.ScrollTop, 0, Math.Max(0, Rows - 1));
                ScrollBottom = Math.Clamp(snapshot.ScrollBottom, 0, Math.Max(0, Rows - 1));
                if (ScrollTop > ScrollBottom)
                {
                    ScrollTop = 0;
                    ScrollBottom = Math.Max(0, Rows - 1);
                }

                Modes.IsAutoWrapMode = snapshot.IsAutoWrapMode;
                Modes.IsApplicationCursorKeys = snapshot.IsApplicationCursorKeys;
                Modes.IsOriginMode = snapshot.IsOriginMode;
                Modes.IsSixelDisplayMode = snapshot.IsSixelDisplayMode;
                Modes.IsBracketedPasteMode = snapshot.IsBracketedPasteMode;
                Modes.IsCursorVisible = snapshot.IsCursorVisible;

                CurrentForeground = TermColor.FromUint(snapshot.CurrentForeground);
                CurrentBackground = TermColor.FromUint(snapshot.CurrentBackground);
                CurrentFgIndex = snapshot.CurrentFgIndex;
                CurrentBgIndex = snapshot.CurrentBgIndex;
                IsDefaultForeground = snapshot.IsDefaultForeground;
                IsDefaultBackground = snapshot.IsDefaultBackground;
                IsInverse = snapshot.IsInverse;
                IsBold = snapshot.IsBold;
                IsFaint = snapshot.IsFaint;
                IsItalic = snapshot.IsItalic;
                IsUnderline = snapshot.IsUnderline;
                // A recording is read from disk, so a value no SGR could have set falls back to the default.
                UnderlineStyle = snapshot.UnderlineStyle is >= 1 and <= 5 ? (UnderlineStyle)snapshot.UnderlineStyle : UnderlineStyle.Single;
                UnderlineColor = snapshot.UnderlineColor is { } penColor ? TermColor.FromUint(penColor) : null;
                UnderlineColorIndex = snapshot.UnderlineColorIndex is >= -1 and <= 255 ? snapshot.UnderlineColorIndex : (short)-1;
                IsBlink = snapshot.IsBlink;
                IsStrikethrough = snapshot.IsStrikethrough;
                IsHidden = snapshot.IsHidden;

                // Cells
                if (legacyCells != null)
                {
                    var cellSpan = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, TerminalCell>(legacyCells.AsSpan());

                    int expectedCells = snapshot.Cols * snapshot.Rows;
                    if (cellSpan.Length >= expectedCells)
                    {
                        int rowsToCopy = Math.Min(snapshot.Rows, _viewport.Length);
                        int colsToCopy = Math.Min(snapshot.Cols, Cols);

                        // Update viewport
                        for (int r = 0; r < rowsToCopy; r++)
                        {
                            var row = _viewport[r];
                            Array.Copy(cellSpan.Slice(r * snapshot.Cols, colsToCopy).ToArray(), row.Cells, colsToCopy);

                            row.WrittenColumns = Math.Min(snapshot.RowWrittenColumns?[r]
                                ?? (snapshot.RowWrapColumns is { } known && known[r] >= 0 ? known[r] : snapshot.Cols), colsToCopy);
                            row.IsWrapped = snapshot.RowWraps != null && r < snapshot.RowWraps.Length && snapshot.RowWraps[r];
                            row.ClearExtendedText();
                            row.ClearHyperlinks();
                            row.ClearUnderlines();
                            row.ClearTextSizing();
                            if (snapshot.RowWrapColumns is { } widths && widths[r] >= 0)
                                row.WrappedColumns = Math.Min(widths[r], colsToCopy);
                            row.TouchRevision();
                            row.ClearExtendedText();
                            row.ClearHyperlinks();
                            row.ClearUnderlines();
                            row.ClearTextSizing();
                        }
                    }
                }

                // Extended Text (Emoji)
                if (snapshot.ExtendedText != null)
                {
                    foreach (var kvp in snapshot.ExtendedText)
                    {
                        int r = kvp.Key / snapshot.Cols;
                        int c = kvp.Key % snapshot.Cols;
                        if (r < _viewport.Length && c < Cols)
                        {
                            _viewport[r].SetExtendedText(c, kvp.Value);
                        }
                    }
                }

                // Underline styles and colors (#270), absent in recordings made before them. Equal entries
                // become one instance, as the cells written with one pen shared one.
                if (snapshot.Underlines != null && snapshot.Cols > 0)
                {
                    var interned = new Dictionary<(byte, uint?, short), CellUnderline?>();
                    foreach (var kvp in snapshot.Underlines)
                    {
                        TerminalUnderlineEntry? entry = kvp.Value;
                        if (kvp.Key < 0 || entry is null || entry.Style is < 1 or > 5 || entry.ColorIndex is < -1 or > 255) continue;

                        int r = kvp.Key / snapshot.Cols;
                        int c = kvp.Key % snapshot.Cols;
                        if (r >= _viewport.Length || c >= Cols) continue;

                        var key = (entry.Style, entry.Color, entry.ColorIndex);
                        if (!interned.TryGetValue(key, out CellUnderline? underline))
                        {
                            // None for a straight underline in the text color, as on the live path.
                            underline = CellUnderline.For(
                                (UnderlineStyle)entry.Style,
                                entry.Color is { } rgb ? TermColor.FromUint(rgb) : null,
                                entry.ColorIndex);
                            interned[key] = underline;
                        }

                        _viewport[r].SetUnderline(c, underline);
                    }
                }

                ImportTextSizingState(ReplaySizingEnvelope(snapshot), activeOnly: true);

                // Restore deferred autowrap ("pw", absent=false in legacy files):
                // a snapshot taken right after output filled the last column must
                // replay with the wrap still pending, or the next character
                // overwrites the row end instead of wrapping — divergence from live.
                _isPendingWrap = snapshot.IsPendingWrap;
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        [System.Diagnostics.Conditional("DEBUG")]
        private void AssertLockHeld()
        {
            if (!Lock.IsReadLockHeld && !Lock.IsWriteLockHeld)
            {
                throw new System.InvalidOperationException("Buffer lock MUST be held for this operation.");
            }
        }
    }
}
