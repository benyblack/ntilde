using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ntilde.VT.Links;
using Ntilde.VT.Storage;

namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        /// <summary>
        /// Captures everything a second buffer needs to apply the same future bytes the same way:
        /// both screens, scrollback (newest <paramref name="maxScrollbackRows"/> rows), tab stops,
        /// saved cursors, modes, kitty keyboard stacks, hyperlink identity and grapheme
        /// continuation. <see cref="TerminalStateSnapshot.Parser"/>,
        /// <see cref="TerminalStateSnapshot.DecoderTail"/> and
        /// <see cref="TerminalStateSnapshot.StreamSeq"/> are left for the caller to fill - the
        /// buffer does not own a parser or a decoder.
        /// </summary>
        public TerminalStateSnapshot ExportState(int maxScrollbackRows)
        {
            Lock.EnterReadLock();
            try
            {
                var links = new HyperlinkTableBuilder();
                var snapshot = new TerminalStateSnapshot
                {
                    Version = TerminalStateSnapshot.CurrentVersion,
                    Cols = Cols,
                    Rows = Rows,
                    CellsSizeOf = Unsafe.SizeOf<TerminalCell>(),
                    CellsLayoutId = TerminalCell.TerminalCellLayoutId,
                    IsAltScreenActive = _isAltScreen,
                    CursorCol = _cursorCol,
                    CursorRow = _cursorRow,
                    IsPendingWrap = _isPendingWrap,
                    ScrollTop = ScrollTop,
                    ScrollBottom = ScrollBottom,
                    TabStops = (bool[])_tabStops.Clone(),
                    SavedCursorMain = ExportCursor(_savedCursors.Main),
                    SavedCursorAlt = ExportCursor(_savedCursors.Alt),
                    ScreenCursorMain = ExportCursor(_screenCursorStates.Main),
                    ScreenCursorAlt = ExportCursor(_screenCursorStates.Alt),
                    RestoreMainCursorOnAltExit = _restoreMainCursorOnAltExit,
                    Sgr = ExportLiveSgrNoLock(),
                    Modes = ExportModes(Modes),
                    KittyKeyboard = new TerminalKittyKeyboardState
                    {
                        MainStack = Modes.KittyKeyboard.ExportMainStackForState(),
                        AltStack = Modes.KittyKeyboard.ExportAltStackForState(),
                        IsAltScreenActive = Modes.KittyKeyboard.IsAltScreenActive,
                    },
                    IsSynchronizedOutput = _isSynchronizedOutput,
                    LastSyncStartUtcTicks = _lastSyncStart.Ticks,
                    Grapheme = new TerminalGraphemeState
                    {
                        HighSurrogate = _highSurrogateBuffer?.ToString(),
                        LastCharCol = _lastCharCol,
                        LastCharRow = _lastCharRow,
                        IsAfterZwj = _isAfterZwj,
                    },
                };

                // Deliberately sequenced rather than folded into the initializer above: the
                // hyperlink table's order is part of the payload, and every export of the same
                // buffer must discover links in the same order or an "export, import, export"
                // comparison sees a difference that is not one.
                (TerminalRow[] mainRows, TerminalRow[] altRows) = ResolveScreensNoLock();
                snapshot.Main = ExportScreenNoLock(mainRows, links);

                // The alt side is shape-normalised on the way out, the main side is not, because
                // the two screens degrade differently when a resize leaves the detached one at the
                // old shape. SwitchToMainScreen crops and pads a wrong-shaped _mainScreen
                // (ResizeDetachedScreenBufferNoLock), so exporting its content is what the source
                // buffer would itself have shown. EnterAltScreen instead *discards* a wrong-shaped
                // _altScreen and allocates a blank one, so exporting that content would resurrect
                // it: the source, on the next `ESC [ ?47h` (clearAlt: false), shows a blank alt
                // screen, while a buffer restored from the raw content would show the carried
                // content in a now-correctly-shaped array that passes the very check that would
                // have discarded it. ?1049h masks this by clearing; ?47h and ?1047h do not.
                snapshot.Alt = ExportScreenNoLock(EnsureScreenShapeNoLock(altRows), links);
                snapshot.Scrollback = ExportScrollbackNoLock(maxScrollbackRows, links, snapshot);
                snapshot.CurrentHyperlinkId = links.IndexOf(_currentHyperlink);
                snapshot.Hyperlinks = links.Entries;
                return snapshot;
            }
            finally
            {
                Lock.ExitReadLock();
            }
        }

        /// <summary>
        /// Adopts <paramref name="snapshot"/> wholesale. The buffer must already be the
        /// snapshot's size; a caller that cannot guarantee that resizes first, and
        /// <see cref="TerminalStateTransfer.Restore"/> does it for you. Derived caches
        /// (the packed style, the render diff) are invalidated rather than carried, so the first
        /// write and the first render recompute them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Wholesale" is literal, which is why inline images and the tracked OSC 133 marks are
        /// <em>cleared</em> rather than left alone. Both are state the snapshot deliberately does
        /// not carry (see <see cref="TerminalStateSnapshot"/>), and both are anchored to rows this
        /// call is about to replace: a retained image would float over a screen it was never
        /// drawn on, and a retained command mark would point Command Assist at a row belonging to
        /// the previous session.
        /// </para>
        /// <para>
        /// Malformed payloads are refused before the write lock is taken, per
        /// <see cref="StateTransferValidation"/>. Nothing here half-applies.
        /// </para>
        /// </remarks>
        /// <returns>
        /// The rebuilt <see cref="Hyperlink"/> identity table, in snapshot order - one instance per
        /// entry, the same instances the restored cells now hold. Hand it to
        /// <see cref="AnsiParser.SeedHyperlinkRegistry"/> on the parser that shares this buffer, or
        /// the first <c>OSC 8</c> in the tail that re-references an <c>id</c> already on screen
        /// mints a second identity for it and those cells stop grouping. Returned rather than
        /// reachable from a property because it describes one import, not the buffer: a later
        /// write can add links this array does not name.
        /// </returns>
        public IReadOnlyList<Hyperlink> ImportState(TerminalStateSnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            // The whole envelope, in one routine shared with TerminalStateTransfer.Restore:
            // every rejection this import can make happens here, outside the write lock and
            // before a single field is written. The decoded blobs and the synchronized-output
            // timestamp come back with it because proving them well-formed *is* decoding them.
            ValidatedBufferState validated = ValidateBufferState(snapshot);
            byte[]? mainCells = validated.MainCells;
            byte[]? altCells = validated.AltCells;
            byte[]? scrollbackCells = validated.ScrollbackCells;
            DateTime lastSyncStart = validated.LastSyncStart;

            Hyperlink[] links;
            bool activeScreenChanged;
            Lock.EnterWriteLock();
            try
            {
                links = RebuildHyperlinks(snapshot.Hyperlinks);

                (TerminalRow[] mainRows, TerminalRow[] altRows) = ResolveScreensNoLock();
                mainRows = EnsureScreenShapeNoLock(mainRows);
                altRows = EnsureScreenShapeNoLock(altRows);

                ImportScreenNoLock(mainRows, snapshot.Main, mainCells, snapshot.Cols, snapshot.Rows, links);
                ImportScreenNoLock(altRows, snapshot.Alt, altCells, snapshot.Cols, snapshot.Rows, links);

                // Normalises the three fields at once: whichever of them was stale before (a
                // main-screen resize replaces _viewport without touching _mainScreen) is now
                // rebuilt from the snapshot, and _viewport is re-pointed at the active one.
                _mainScreen = mainRows;
                _altScreen = altRows;

                // Read before the assignment, raised after the lock: an import that lands on the
                // other screen is a screen switch as far as every subscriber is concerned, and
                // the imported tail does not contain the `?1049h` that would otherwise announce
                // it. See the OnScreenSwitched call at the end of this method.
                activeScreenChanged = _isAltScreen != snapshot.IsAltScreenActive;
                _isAltScreen = snapshot.IsAltScreenActive;
                _viewport = _isAltScreen ? _altScreen : _mainScreen;

                ImportScrollbackNoLock(snapshot, scrollbackCells, links);
                ImportTabStopsNoLock(snapshot.TabStops);

                _cursorCol = Math.Clamp(snapshot.CursorCol, 0, Math.Max(0, Cols - 1));
                _cursorRow = Math.Clamp(snapshot.CursorRow, 0, Math.Max(0, Rows - 1));
                _isPendingWrap = snapshot.IsPendingWrap;
                ScrollTop = Math.Clamp(snapshot.ScrollTop, 0, Math.Max(0, Rows - 1));
                ScrollBottom = Math.Clamp(snapshot.ScrollBottom, 0, Math.Max(0, Rows - 1));
                if (ScrollTop > ScrollBottom)
                {
                    ScrollTop = 0;
                    ScrollBottom = Math.Max(0, Rows - 1);
                }

                ImportCursor(_savedCursors.Main, snapshot.SavedCursorMain);
                ImportCursor(_savedCursors.Alt, snapshot.SavedCursorAlt);
                ImportCursor(_screenCursorStates.Main, snapshot.ScreenCursorMain);
                ImportCursor(_screenCursorStates.Alt, snapshot.ScreenCursorAlt);
                _restoreMainCursorOnAltExit = snapshot.RestoreMainCursorOnAltExit;

                ImportLiveSgrNoLock(snapshot.Sgr);
                _currentHyperlink = snapshot.CurrentHyperlinkId >= 0
                    && snapshot.CurrentHyperlinkId < links.Length
                        ? links[snapshot.CurrentHyperlinkId]
                        : null;

                ImportModes(Modes, snapshot.Modes);
                Modes.KittyKeyboard.ImportStacksForState(
                    snapshot.KittyKeyboard.MainStack,
                    snapshot.KittyKeyboard.AltStack,
                    snapshot.KittyKeyboard.IsAltScreenActive);

                _isSynchronizedOutput = snapshot.IsSynchronizedOutput;
                _lastSyncStart = lastSyncStart;

                _highSurrogateBuffer = string.IsNullOrEmpty(snapshot.Grapheme.HighSurrogate)
                    ? null
                    : snapshot.Grapheme.HighSurrogate[0];
                _lastCharCol = snapshot.Grapheme.LastCharCol;
                _lastCharRow = snapshot.Grapheme.LastCharRow;
                _isAfterZwj = snapshot.Grapheme.IsAfterZwj;

                // Excluded from the payload, and therefore cleared rather than kept. Inline images
                // are decoder-owned handles anchored to absolute rows this import just replaced,
                // so keeping them would leave pixels floating over a screen they were never drawn
                // on. Retired through the same queue ClearImages uses, not disposed here: a render
                // pass may still be holding these handles.
                foreach (TerminalImage image in _images)
                {
                    RetireImage(image);
                }

                _images.Clear();

                // Derived, never carried: the packed style cache is rebuilt from the SGR fields
                // above on the next write, and the render-diff cache stays cold so the first
                // CaptureRenderSnapshot is a full repaint rather than a diff against rows that
                // were never on this screen.
                _isStyleDirty = true;
                _hasSnapshotState = false;
            }
            finally
            {
                Lock.ExitWriteLock();
            }

            // Also excluded from the payload (see TerminalStateSnapshot's remarks: the marks carry
            // a process-local scrollback generation and a 133;B re-arms them). Cleared outside the
            // write lock because it takes its own gate, and after it because a mark pointing into
            // the replaced screen is exactly what must not survive.
            ClearTrackedShellMarks();

            Invalidate();

            // The transition notification EnterAltScreen and SwitchToMainScreen raise, for the
            // transition an import can perform without either of them running. Attaching an
            // alt-screen snapshot to a pane whose buffer is on the main screen leaves the assist
            // UI in main-screen state - Command Assist and the Agent Output panel visible over a
            // full-screen TUI, agent status never told - because the tail carries no enter-alt
            // sequence to announce the switch later (AGENTS.md: assist UI auto-hides in
            // alternate-screen mode).
            //
            // On an actual transition only. Firing unconditionally would report a switch on every
            // attach, and `?1049h`-on-`?1049h` is precisely what EnterAltScreen's own early-out
            // declines to announce.
            //
            // Outside the write lock, unlike the two live switch sites, and for the reason
            // ImportState already places Invalidate() here rather than inside: Lock is
            // NoRecursion, so a subscriber that reaches back into the buffer from its handler -
            // TerminalView.OnScreenSwitched reads it, and its scroll-to-cursor continuation goes
            // further - throws LockRecursionException against a lock this method still holds.
            // That hazard is pre-existing at the two live sites and is not this change's to fix;
            // it is a hazard a new call site has no reason to inherit.
            //
            // After Invalidate(), matching both live sites: a handler that reacts by dropping
            // caches and asking for a repaint should find the repaint already requested.
            if (activeScreenChanged)
            {
                OnScreenSwitched?.Invoke(snapshot.IsAltScreenActive);
            }

            return links;
        }

        // ── screens ──────────────────────────────────────────────────────────────

        /// <summary>
        /// The two screen row arrays, main first, resolved through <c>_viewport</c> rather than
        /// read straight off <c>_mainScreen</c> / <c>_altScreen</c>.
        /// </summary>
        /// <remarks>
        /// <c>_viewport</c> is the only field guaranteed to be the live screen. The detached one
        /// is reconciled lazily, on the next screen switch: a main-screen resize replaces
        /// <c>_viewport</c> and leaves <c>_mainScreen</c> pointing at the pre-resize array
        /// (<c>TerminalBuffer.ResizeAndReflow.cs</c>, the "Normal Main Screen Resize" branch), and
        /// an alt-screen resize does the same to <c>_altScreen</c>. Exporting the raw fields would
        /// therefore ship a stale screen after any resize. Whichever field currently aliases
        /// <c>_viewport</c> is authoritative, so read the active screen from <c>_viewport</c> and
        /// only the inactive one from its field.
        /// </remarks>
        private (TerminalRow[] Main, TerminalRow[] Alt) ResolveScreensNoLock()
        {
            return _isAltScreen
                ? (_mainScreen, _viewport)
                : (_viewport, _altScreen);
        }

        /// <summary>
        /// Returns <paramref name="rows"/> if it already matches this buffer's size, or a freshly
        /// built blank screen of the right shape if it does not (a detached screen can be left at a
        /// pre-resize size until the next switch).
        /// </summary>
        /// <remarks>
        /// Blank-on-mismatch rather than crop-and-pad, because this is <c>EnterAltScreen</c>'s rule
        /// (<c>TerminalBuffer.AccessAndSnapshot.cs:666-673</c>): a wrong-shaped alt screen is
        /// discarded, not resized. Used on both sides of the transfer - to normalise the alt screen
        /// on the way out, so an export never ships content the source itself would have thrown
        /// away, and to give the import a correctly-shaped target to write into. The main screen
        /// does <em>not</em> go through here on export: <c>SwitchToMainScreen</c> crops and pads it
        /// instead, so its content survives a shape change and must be exported.
        /// </remarks>
        private TerminalRow[] EnsureScreenShapeNoLock(TerminalRow[]? rows)
        {
            if (rows != null && rows.Length == Rows && (Rows == 0 || rows[0].Cells.Length == Cols))
            {
                return rows;
            }

            var rebuilt = new TerminalRow[Rows];
            for (int r = 0; r < Rows; r++)
            {
                rebuilt[r] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);
            }

            return rebuilt;
        }

        private TerminalScreenState ExportScreenNoLock(TerminalRow[] rows, HyperlinkTableBuilder links)
        {
            int cols = Cols;
            int rowCount = Rows;
            var cells = new TerminalCell[Math.Max(0, rowCount * cols)];
            Array.Fill(cells, TerminalCell.Default);
            var wraps = new bool[Math.Max(0, rowCount)];
            Dictionary<int, string>? extendedText = null;
            Dictionary<int, int>? linkIds = null;

            for (int r = 0; r < rowCount && r < rows.Length; r++)
            {
                TerminalRow row = rows[r];
                int copy = Math.Min(cols, row.Cells.Length);
                Array.Copy(row.Cells, 0, cells, r * cols, copy);
                wraps[r] = row.IsWrapped;

                if (!row.HasRowMetadata)
                {
                    continue;
                }

                AppendSideTables(
                    row.GetExtendedTextMap(),
                    row.GetHyperlinkMap(),
                    r * cols,
                    cols,
                    links,
                    ref extendedText,
                    ref linkIds);
            }

            return new TerminalScreenState
            {
                CellsBase64 = EncodeCells(cells),
                RowWraps = wraps,
                ExtendedText = extendedText,
                LinkIds = linkIds,
            };
        }

        private void ImportScreenNoLock(
            TerminalRow[] rows,
            TerminalScreenState state,
            byte[]? blob,
            int sourceCols,
            int sourceRows,
            Hyperlink[] links)
        {
            for (int r = 0; r < rows.Length; r++)
            {
                TerminalRow row = rows[r];
                row.IsWrapped = false;
                row.ClearExtendedText();
                row.ClearHyperlinks();
                for (int c = 0; c < row.Cells.Length; c++)
                {
                    row.Cells[c] = TerminalCell.Default;
                }

                row.TouchRevision();
            }

            if (blob is null || blob.Length == 0 || sourceCols <= 0)
            {
                return;
            }

            ReadOnlySpan<TerminalCell> source = MemoryMarshal.Cast<byte, TerminalCell>(blob);
            int availableRows = source.Length / sourceCols;
            int rowsToCopy = Math.Min(Math.Min(sourceRows, availableRows), rows.Length);
            int colsToCopy = Math.Min(sourceCols, Cols);

            for (int r = 0; r < rowsToCopy; r++)
            {
                TerminalRow row = rows[r];
                source.Slice(r * sourceCols, Math.Min(colsToCopy, row.Cells.Length))
                      .CopyTo(row.Cells.AsSpan());

                if (state.RowWraps != null && r < state.RowWraps.Length)
                {
                    row.IsWrapped = state.RowWraps[r];
                }
            }

            // rowsToCopy, not rows.Length: a row past it had its cells deliberately left at
            // TerminalCell.Default (the source had no such row, or the destination screen is
            // taller than the snapshot's), so applying the snapshot's extended text or link ids
            // to it would decorate a row whose content was never restored.
            if (state.ExtendedText != null)
            {
                foreach (KeyValuePair<int, string> kv in state.ExtendedText)
                {
                    if (!TrySplitKey(kv.Key, sourceCols, rowsToCopy, colsToCopy, out int r, out int c))
                    {
                        continue;
                    }

                    rows[r].SetExtendedText(c, kv.Value);
                }
            }

            if (state.LinkIds != null)
            {
                foreach (KeyValuePair<int, int> kv in state.LinkIds)
                {
                    if (!TrySplitKey(kv.Key, sourceCols, rowsToCopy, colsToCopy, out int r, out int c))
                    {
                        continue;
                    }

                    if ((uint)kv.Value < (uint)links.Length)
                    {
                        rows[r].SetHyperlink(c, links[kv.Value]);
                    }
                }
            }
        }

        // ── scrollback ───────────────────────────────────────────────────────────

        private TerminalScreenState ExportScrollbackNoLock(
            int maxScrollbackRows,
            HyperlinkTableBuilder links,
            TerminalStateSnapshot snapshot)
        {
            int cols = Cols;
            int count = _scrollback.Count;
            int cap = Math.Max(0, maxScrollbackRows);
            int keep = Math.Min(count, cap);

            // The rows kept are the NEWEST ones: a user scrolling up meets those first, so an
            // attach that can only afford part of the history should spend it on the part that
            // will actually be looked at.
            int first = count - keep;

            snapshot.ScrollbackRowCount = keep;
            snapshot.ScrollbackTruncated = count > cap;
            snapshot.ScrollbackRowsDropped = Math.Max(0, count - cap);

            if (keep <= 0 || cols <= 0)
            {
                return new TerminalScreenState { RowWraps = [] };
            }

            var cells = new TerminalCell[keep * cols];
            var wraps = new bool[keep];
            Dictionary<int, string>? extendedText = null;
            Dictionary<int, int>? linkIds = null;

            for (int i = 0; i < keep; i++)
            {
                int logical = first + i;
                ReadOnlySpan<TerminalCell> row = _scrollback.GetRow(logical);
                int copy = Math.Min(cols, row.Length);
                row.Slice(0, copy).CopyTo(cells.AsSpan(i * cols, cols));
                if (copy < cols)
                {
                    // Never happens while the store's width tracks the buffer's, but a zeroed
                    // TerminalCell is not a blank one - it has no DefaultForeground flag - so a
                    // narrow row must be padded rather than left at the array's zero fill.
                    cells.AsSpan((i * cols) + copy, cols - copy).Fill(TerminalCell.Default);
                }

                wraps[i] = _scrollback.IsRowWrapped(logical);

                AppendSideTables(
                    _scrollback.GetExtendedTextMap(logical),
                    _scrollback.GetHyperlinkMap(logical),
                    i * cols,
                    cols,
                    links,
                    ref extendedText,
                    ref linkIds);
            }

            return new TerminalScreenState
            {
                CellsBase64 = EncodeCells(cells),
                RowWraps = wraps,
                ExtendedText = extendedText,
                LinkIds = linkIds,
            };
        }

        private void ImportScrollbackNoLock(
            TerminalStateSnapshot snapshot, byte[]? blob, Hyperlink[] links)
        {
            _scrollback.Clear();

            TerminalScreenState state = snapshot.Scrollback;
            int sourceCols = snapshot.Cols;
            if (blob is null || blob.Length == 0 || sourceCols <= 0 || Cols <= 0)
            {
                return;
            }

            ReadOnlySpan<TerminalCell> source = MemoryMarshal.Cast<byte, TerminalCell>(blob);
            int rowCount = Math.Min(snapshot.ScrollbackRowCount, source.Length / sourceCols);
            if (rowCount <= 0)
            {
                return;
            }

            int colsToCopy = Math.Min(sourceCols, Cols);
            List<KeyValuePair<int, string>>?[] extByRow = GroupByRow(state.ExtendedText, sourceCols, rowCount);
            List<KeyValuePair<int, int>>?[] linkByRow = GroupByRow(state.LinkIds, sourceCols, rowCount);

            var buffer = new TerminalCell[Cols];
            for (int i = 0; i < rowCount; i++)
            {
                Array.Fill(buffer, TerminalCell.Default);
                source.Slice(i * sourceCols, colsToCopy).CopyTo(buffer.AsSpan());

                SmallMap<string>? extended = null;
                if (extByRow[i] is { } extEntries)
                {
                    foreach (KeyValuePair<int, string> entry in extEntries)
                    {
                        if (entry.Key >= colsToCopy) continue;
                        (extended ??= new SmallMap<string>()).Set(entry.Key, entry.Value);
                    }
                }

                SmallMap<Hyperlink>? hyperlinks = null;
                if (linkByRow[i] is { } linkEntries)
                {
                    foreach (KeyValuePair<int, int> entry in linkEntries)
                    {
                        if (entry.Key >= colsToCopy) continue;
                        if ((uint)entry.Value >= (uint)links.Length) continue;
                        (hyperlinks ??= new SmallMap<Hyperlink>()).Set(entry.Key, links[entry.Value]);
                    }
                }

                bool wrapped = state.RowWraps != null && i < state.RowWraps.Length && state.RowWraps[i];
                _scrollback.AppendRow(buffer, wrapped, extended, hyperlinks);
            }
        }

        // ── tab stops ────────────────────────────────────────────────────────────

        /// <summary>
        /// Replaces <c>_tabStops</c>, widening or narrowing to this buffer's width the way
        /// <c>ResizeTabStopsNoLock</c> does: the overlap is carried and any new columns get the
        /// every-eight default. A snapshot taken at a different width then degrades exactly as a
        /// resize would, rather than arriving with no stops at all.
        /// </summary>
        private void ImportTabStopsNoLock(bool[] incoming)
        {
            var stops = new bool[Math.Max(0, Cols)];
            int shared = Math.Min(stops.Length, incoming.Length);
            for (int col = 0; col < shared; col++)
            {
                stops[col] = incoming[col];
            }

            for (int col = shared; col < stops.Length; col++)
            {
                stops[col] = IsDefaultTabStopColumn(col);
            }

            _tabStops = stops;
        }

        // ── cursors, SGR, modes ──────────────────────────────────────────────────

        private static TerminalCursorState ExportCursor(CursorState cursor) => new()
        {
            Row = cursor.Row,
            Col = cursor.Col,
            IsPendingWrap = cursor.IsPendingWrap,
            Sgr = new TerminalSgrState
            {
                Foreground = cursor.Foreground.ToUint(),
                Background = cursor.Background.ToUint(),
                FgIndex = cursor.FgIndex,
                BgIndex = cursor.BgIndex,
                IsDefaultForeground = cursor.IsDefaultForeground,
                IsDefaultBackground = cursor.IsDefaultBackground,
                IsInverse = cursor.IsInverse,
                IsBold = cursor.IsBold,
                IsFaint = cursor.IsFaint,
                IsItalic = cursor.IsItalic,
                IsUnderline = cursor.IsUnderline,
                IsBlink = cursor.IsBlink,
                IsStrikethrough = cursor.IsStrikethrough,
                IsHidden = cursor.IsHidden,
            },
        };

        private static void ImportCursor(CursorState target, TerminalCursorState source)
        {
            TerminalSgrState sgr = source.Sgr;
            target.Row = source.Row;
            target.Col = source.Col;
            target.IsPendingWrap = source.IsPendingWrap;
            target.Foreground = TermColor.FromUint(sgr.Foreground);
            target.Background = TermColor.FromUint(sgr.Background);
            target.FgIndex = sgr.FgIndex;
            target.BgIndex = sgr.BgIndex;
            target.IsDefaultForeground = sgr.IsDefaultForeground;
            target.IsDefaultBackground = sgr.IsDefaultBackground;
            target.IsInverse = sgr.IsInverse;
            target.IsBold = sgr.IsBold;
            target.IsFaint = sgr.IsFaint;
            target.IsItalic = sgr.IsItalic;
            target.IsUnderline = sgr.IsUnderline;
            target.IsBlink = sgr.IsBlink;
            target.IsStrikethrough = sgr.IsStrikethrough;
            target.IsHidden = sgr.IsHidden;
        }

        private TerminalSgrState ExportLiveSgrNoLock() => new()
        {
            Foreground = _currentForeground.ToUint(),
            Background = _currentBackground.ToUint(),
            FgIndex = _currentFgIndex,
            BgIndex = _currentBgIndex,
            IsDefaultForeground = _isDefaultForeground,
            IsDefaultBackground = _isDefaultBackground,
            IsInverse = _isInverse,
            IsBold = _isBold,
            IsFaint = _isFaint,
            IsItalic = _isItalic,
            IsUnderline = _isUnderline,
            IsBlink = _isBlink,
            IsStrikethrough = _isStrikethrough,
            IsHidden = _isHidden,
        };

        /// <summary>
        /// Writes the live SGR set straight to the backing fields rather than through the public
        /// properties. The property setters have side effects on each other - assigning
        /// <c>CurrentForeground</c> clears <c>IsDefaultForeground</c>, because an SGR that names a
        /// colour means the colour is explicit - so restoring through them makes the result depend
        /// on statement order. The fields carry no such coupling.
        /// </summary>
        private void ImportLiveSgrNoLock(TerminalSgrState sgr)
        {
            _currentForeground = TermColor.FromUint(sgr.Foreground);
            _currentBackground = TermColor.FromUint(sgr.Background);
            _currentFgIndex = sgr.FgIndex;
            _currentBgIndex = sgr.BgIndex;
            _isDefaultForeground = sgr.IsDefaultForeground;
            _isDefaultBackground = sgr.IsDefaultBackground;
            _isInverse = sgr.IsInverse;
            _isBold = sgr.IsBold;
            _isFaint = sgr.IsFaint;
            _isItalic = sgr.IsItalic;
            _isUnderline = sgr.IsUnderline;
            _isBlink = sgr.IsBlink;
            _isStrikethrough = sgr.IsStrikethrough;
            _isHidden = sgr.IsHidden;
        }

        private static TerminalModeState ExportModes(ModeState modes) => new()
        {
            MouseModeX10 = modes.MouseModeX10,
            MouseModeButtonEvent = modes.MouseModeButtonEvent,
            MouseModeAnyEvent = modes.MouseModeAnyEvent,
            MouseModeSGR = modes.MouseModeSGR,
            IsApplicationCursorKeys = modes.IsApplicationCursorKeys,
            IsAutoWrapMode = modes.IsAutoWrapMode,
            IsOriginMode = modes.IsOriginMode,
            IsFocusEventReporting = modes.IsFocusEventReporting,
            IsBracketedPasteMode = modes.IsBracketedPasteMode,
            IsCursorVisible = modes.IsCursorVisible,
            IsCursorBlinkEnabled = modes.IsCursorBlinkEnabled,
            CursorStyle = (int)modes.CursorStyle,
            IsInsertMode = modes.IsInsertMode,
            IsLineFeedNewLineMode = modes.IsLineFeedNewLineMode,
            IsEchoEnabled = modes.IsEchoEnabled,
        };

        private static void ImportModes(ModeState target, TerminalModeState source)
        {
            target.MouseModeX10 = source.MouseModeX10;
            target.MouseModeButtonEvent = source.MouseModeButtonEvent;
            target.MouseModeAnyEvent = source.MouseModeAnyEvent;
            target.MouseModeSGR = source.MouseModeSGR;
            target.IsApplicationCursorKeys = source.IsApplicationCursorKeys;
            target.IsAutoWrapMode = source.IsAutoWrapMode;
            target.IsOriginMode = source.IsOriginMode;
            target.IsFocusEventReporting = source.IsFocusEventReporting;
            target.IsBracketedPasteMode = source.IsBracketedPasteMode;
            target.IsCursorVisible = source.IsCursorVisible;
            target.IsCursorBlinkEnabled = source.IsCursorBlinkEnabled;
            target.CursorStyle = (CursorStyle)source.CursorStyle;
            target.IsInsertMode = source.IsInsertMode;
            target.IsLineFeedNewLineMode = source.IsLineFeedNewLineMode;
            target.IsEchoEnabled = source.IsEchoEnabled;
        }

        // ── validation ───────────────────────────────────────────────────────────

        /// <summary>
        /// Refuses a payload this build cannot read.        /// <summary>
        /// What a validated snapshot hands the import: the three cell blobs, already decoded and
        /// proven well-formed, and the synchronized-output timestamp, already proven
        /// representable. Returned rather than recomputed because proving them well-formed *is*
        /// decoding them, and doing it twice inside one import would be the expensive half of the
        /// payload paid twice.
        /// </summary>
        internal readonly struct ValidatedBufferState
        {
            internal ValidatedBufferState(
                byte[]? mainCells, byte[]? altCells, byte[]? scrollbackCells, DateTime lastSyncStart)
            {
                MainCells = mainCells;
                AltCells = altCells;
                ScrollbackCells = scrollbackCells;
                LastSyncStart = lastSyncStart;
            }

            internal byte[]? MainCells { get; }

            internal byte[]? AltCells { get; }

            internal byte[]? ScrollbackCells { get; }

            internal DateTime LastSyncStart { get; }
        }

        /// <summary>
        /// The single routine that decides whether a <see cref="TerminalStateSnapshot"/>'s buffer
        /// half is acceptable. Every rejection <see cref="ImportState"/> can make is made here,
        /// before it takes its write lock and before it writes a field.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Internal, and factored out of <see cref="ImportState"/>, so
        /// <see cref="TerminalStateTransfer.Restore"/> can run <em>the whole thing</em> before it
        /// resizes the destination buffer. Hoisting a subset is not enough and was the bug this
        /// replaced: a snapshot whose version and layout were fine but whose scrollback blob was
        /// not valid base64 would pass the hoisted half, get the destination resized and reflowed,
        /// and only then be refused by the decode inside the import.
        /// </para>
        /// <para>
        /// <see cref="ImportState"/> calls it too. That is deliberate belt-and-braces, not
        /// redundancy to optimise away: <see cref="ImportState"/> is public and independently
        /// callable, so it validates its own input regardless of what a caller already checked.
        /// </para>
        /// </remarks>
        internal static ValidatedBufferState ValidateBufferState(TerminalStateSnapshot snapshot)
        {
            ValidateVersionAndCellLayout(snapshot);
            ValidateRequiredStructure(snapshot);

            // Ordered after the structure pass on purpose: the three CellsBase64 reads below
            // dereference Main/Alt/Scrollback, which the pass has just proven present.
            byte[]? mainCells = StateTransferValidation.DecodeBlob(
                snapshot.Main.CellsBase64, "main screen cells");
            byte[]? altCells = StateTransferValidation.DecodeBlob(
                snapshot.Alt.CellsBase64, "alternate screen cells");
            byte[]? scrollbackCells = StateTransferValidation.DecodeBlob(
                snapshot.Scrollback.CellsBase64, "scrollback cells");
            DateTime lastSyncStart = StateTransferValidation.UtcFromTicks(
                snapshot.LastSyncStartUtcTicks, "synchronized-output start");

            // Valid base64 is not yet a valid screen. The blob is reinterpreted whole by
            // MemoryMarshal.Cast against the geometry the envelope declares, so its length is
            // structure, not a count: a short or cell-misaligned blob describes a screen the
            // sender never had. Left unchecked, ImportScreenNoLock blanks the destination and then
            // copies only the rows that happened to arrive, turning a truncated payload into a
            // partially erased terminal rather than a refusal - and ImportScrollbackNoLock does
            // the same against ScrollbackRowCount. Checked here so the refusal still lands before
            // the write lock and before Restore's resize.
            ValidateCellBlobLength(mainCells, snapshot, snapshot.Rows, "main screen cells");
            ValidateCellBlobLength(altCells, snapshot, snapshot.Rows, "alternate screen cells");
            ValidateCellBlobLength(
                scrollbackCells, snapshot, snapshot.ScrollbackRowCount, "scrollback cells");

            return new ValidatedBufferState(mainCells, altCells, scrollbackCells, lastSyncStart);
        }

        /// <summary>
        /// Rejects a decoded cell blob whose byte count is not exactly
        /// <c>Cols * <paramref name="declaredRows"/></c> whole <see cref="TerminalCell"/> structs.
        /// </summary>
        /// <remarks>
        /// Exact, not "at least": a longer-than-declared blob is as malformed as a short one here,
        /// because nothing in the envelope says what the surplus is. The legacy
        /// <c>TerminalBuffer.ApplySnapshot</c> replay path deliberately tolerates a longer blob -
        /// it reads recorded files already on disk - and is left alone; this is the import path,
        /// and its policy is to reject malformed structure.
        /// <para>
        /// A missing blob is length zero and is therefore accepted only where zero is what the
        /// geometry asks for, which is exactly the empty-scrollback export. With
        /// <c>Cols</c> and <c>Rows</c> already proven positive, an absent main or alternate screen
        /// can never satisfy it.
        /// </para>
        /// </remarks>
        private static void ValidateCellBlobLength(
            byte[]? blob, TerminalStateSnapshot snapshot, int declaredRows, string what)
        {
            long expectedBytes = (long)snapshot.Cols * declaredRows * Unsafe.SizeOf<TerminalCell>();
            long actualBytes = blob?.LongLength ?? 0L;
            if (actualBytes == expectedBytes)
            {
                return;
            }

            throw StateTransferValidation.Reject(
                what,
                $"{actualBytes} decoded bytes, expected {expectedBytes} for " +
                $"{snapshot.Cols}x{declaredRows} at cells_sizeof={snapshot.CellsSizeOf}");
        }

        /// <summary>
        /// The structure half of <see cref="StateTransferValidation"/>'s rule applied to every
        /// required member of the envelope, nested DTOs included.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Required nested objects are rejected when null, never tolerated.</b> Every one of
        /// these properties is non-nullable with a <c>= new()</c> initialiser, so a normal
        /// serialize/deserialize round trip cannot produce null here; only an explicit
        /// <c>"modes": null</c> in the payload can. Tolerating that was worse than throwing in
        /// two different ways, and both were live before this pass: the four cursor states and
        /// the mode state returned early and therefore <em>kept the destination's own</em>, so a
        /// restore into a reused buffer produced a hybrid terminal whose insert mode, autowrap
        /// and mouse reporting came from the previous session; the live SGR state substituted
        /// defaults, so the terminal adopted attributes the sender never described. A snapshot
        /// that cannot express modes cannot be adopted wholesale, which is the only thing
        /// <see cref="ImportState"/> offers, so it is refused instead.
        /// </para>
        /// <para>
        /// <b>Enum ordinals and table indices are structure too.</b> <c>CursorStyle</c> selects a
        /// caret shape and <c>CurrentHyperlinkId</c> / the per-cell <c>LinkIds</c> values select
        /// entries of the hyperlink table; none of them has a defensible clamp, and each fails
        /// far from the import that set it - or, worse, does not fail at all and silently drops a
        /// link the payload named.
        /// </para>
        /// </remarks>
        private static void ValidateRequiredStructure(TerminalStateSnapshot snapshot)
        {
            TerminalScreenState main = StateTransferValidation.RequirePresent(
                snapshot.Main, "main screen state");
            TerminalScreenState alt = StateTransferValidation.RequirePresent(
                snapshot.Alt, "alternate screen state");
            TerminalScreenState scrollback = StateTransferValidation.RequirePresent(
                snapshot.Scrollback, "scrollback state");
            StateTransferValidation.RequirePresent(snapshot.TabStops, "tab stops");

            ValidateCursorState(snapshot.SavedCursorMain, "saved cursor (main screen)");
            ValidateCursorState(snapshot.SavedCursorAlt, "saved cursor (alternate screen)");
            ValidateCursorState(snapshot.ScreenCursorMain, "screen cursor (main screen)");
            ValidateCursorState(snapshot.ScreenCursorAlt, "screen cursor (alternate screen)");

            StateTransferValidation.RequirePresent(snapshot.Sgr, "live SGR state");

            TerminalModeState modes = StateTransferValidation.RequirePresent(
                snapshot.Modes, "mode state");
            if (!Enum.IsDefined((CursorStyle)modes.CursorStyle))
            {
                throw StateTransferValidation.Reject(
                    "mode state cursor style", $"cstyle={modes.CursorStyle} is not a defined style");
            }

            TerminalKittyKeyboardState kitty = StateTransferValidation.RequirePresent(
                snapshot.KittyKeyboard, "kitty keyboard state");
            StateTransferValidation.RequirePresent(kitty.MainStack, "kitty keyboard main stack");
            StateTransferValidation.RequirePresent(kitty.AltStack, "kitty keyboard alternate stack");

            StateTransferValidation.RequirePresent(snapshot.Grapheme, "grapheme continuation state");

            List<TerminalHyperlinkEntry> hyperlinks = StateTransferValidation.RequirePresent(
                snapshot.Hyperlinks, "hyperlink table");
            for (int i = 0; i < hyperlinks.Count; i++)
            {
                TerminalHyperlinkEntry entry = StateTransferValidation.RequirePresent(
                    hyperlinks[i], $"hyperlink table entry {i}");
                StateTransferValidation.RequirePresent(entry.Uri, $"hyperlink table entry {i} URI");
            }

            // -1 is the "cursor is not writing under a link" sentinel, so the range starts there.
            // An empty table therefore admits -1 and nothing else.
            StateTransferValidation.RequireInRange(
                snapshot.CurrentHyperlinkId, -1, hyperlinks.Count - 1, "current hyperlink index");

            ValidateSideTables(main, hyperlinks.Count, "main screen");
            ValidateSideTables(alt, hyperlinks.Count, "alternate screen");
            ValidateSideTables(scrollback, hyperlinks.Count, "scrollback");
        }

        private static void ValidateCursorState(TerminalCursorState? cursor, string what)
        {
            TerminalCursorState present = StateTransferValidation.RequirePresent(cursor, what);
            StateTransferValidation.RequirePresent(present.Sgr, $"{what} SGR");
        }

        /// <summary>
        /// The per-cell side tables. Their <em>keys</em> are deliberately not validated - a key
        /// outside the copied region is dropped rather than refused, because a snapshot imported
        /// into a differently-shaped buffer legitimately has rows it cannot restore. Their
        /// <em>values</em> are: a null grapheme cluster and a link index naming no table entry are
        /// both malformed however the geometry lands.
        /// </summary>
        private static void ValidateSideTables(TerminalScreenState state, int linkCount, string what)
        {
            if (state.ExtendedText is { } extended)
            {
                foreach (KeyValuePair<int, string> entry in extended)
                {
                    if (entry.Value is null)
                    {
                        throw StateTransferValidation.Reject(
                            $"{what} extended text", $"cell {entry.Key} carries a null cluster");
                    }
                }
            }

            if (state.LinkIds is { } linkIds)
            {
                foreach (KeyValuePair<int, int> entry in linkIds)
                {
                    if ((uint)entry.Value >= (uint)linkCount)
                    {
                        throw StateTransferValidation.Reject(
                            $"{what} link index",
                            $"cell {entry.Key} names link {entry.Value} of {linkCount}");
                    }
                }
            }
        }

        /// <summary>
        /// Refuses a payload this build cannot read. The cell check mirrors
        /// <c>ReplayRunner.ValidateSnapshotCellLayout</c>: the blob is reinterpreted with
        /// <see cref="MemoryMarshal.Cast{TFrom, TTo}(ReadOnlySpan{TFrom})"/>, which will happily
        /// read a struct laid out differently and hand back plausible garbage, so the mismatch has
        /// to be caught here or not at all.
        /// </summary>
        /// <remarks>
        /// The structure half of <see cref="StateTransferValidation"/>'s rule for the buffer
        /// payload: a format version, a struct layout id and the geometry every row/column index
        /// in the payload is expressed in. Sizes and counts inside those dimensions - the cursor,
        /// the scroll region, the scrollback row count - are clamped by the import instead.
        /// </remarks>
        private static void ValidateVersionAndCellLayout(TerminalStateSnapshot snapshot)
        {
            if (snapshot.Version != TerminalStateSnapshot.CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Terminal state version mismatch: expected v={TerminalStateSnapshot.CurrentVersion}, " +
                    $"actual v={snapshot.Version}.");
            }

            // Geometry is structure, not a count: every ExtendedText/LinkIds key is a
            // `row * Cols + col` encoding, so a non-positive Cols makes the whole side-table
            // coordinate space meaningless rather than merely smaller. It is also the dimension
            // TerminalStateTransfer.Restore resizes the destination buffer to.
            if (snapshot.Cols <= 0 || snapshot.Rows <= 0)
            {
                throw StateTransferValidation.Reject(
                    "snapshot geometry", $"{snapshot.Cols}x{snapshot.Rows} is not a usable screen");
            }

            int expectedSizeOf = Unsafe.SizeOf<TerminalCell>();
            string expectedLayoutId = TerminalCell.TerminalCellLayoutId;
            bool sizeMatches = snapshot.CellsSizeOf == expectedSizeOf;
            bool layoutMatches = string.Equals(snapshot.CellsLayoutId, expectedLayoutId, StringComparison.Ordinal);
            if (sizeMatches && layoutMatches)
            {
                return;
            }

            string actualLayoutLabel = string.IsNullOrEmpty(snapshot.CellsLayoutId)
                ? "<missing>"
                : snapshot.CellsLayoutId;
            throw new InvalidOperationException(
                $"Terminal state cell layout mismatch: expected cells_sizeof={expectedSizeOf}, " +
                $"cells_layout_id={expectedLayoutId}; actual cells_sizeof={snapshot.CellsSizeOf}, " +
                $"cells_layout_id={actualLayoutLabel}.");
        }

        // ── hyperlink identity ───────────────────────────────────────────────────

        /// <summary>
        /// Interns the <see cref="Hyperlink"/> instances an export meets, by reference.
        /// </summary>
        /// <remarks>
        /// Reference equality, not value equality, because that is what OSC 8 identity is: two
        /// no-id links to the same URI are equal field-for-field and are still two links. The
        /// comparer therefore has to be <see cref="ReferenceEqualityComparer"/>, and the key type
        /// <see cref="object"/> along with it - <c>ReferenceEqualityComparer.Instance</c> is an
        /// <c>IEqualityComparer&lt;object&gt;</c> and <c>IEqualityComparer&lt;T&gt;</c> is
        /// invariant.
        /// </remarks>
        private sealed class HyperlinkTableBuilder
        {
            private readonly Dictionary<object, int> _byReference = new(ReferenceEqualityComparer.Instance);

            public List<TerminalHyperlinkEntry> Entries { get; } = [];

            public int IndexOf(Hyperlink? link)
            {
                if (link is null)
                {
                    return -1;
                }

                if (_byReference.TryGetValue(link, out int existing))
                {
                    return existing;
                }

                int index = Entries.Count;
                _byReference[link] = index;
                Entries.Add(new TerminalHyperlinkEntry { Uri = link.Uri, Id = link.Id });
                return index;
            }
        }

        /// <summary>
        /// Builds exactly one <see cref="Hyperlink"/> per table entry. Every cell naming that
        /// index is then handed the same instance, which is what keeps an OSC 8 anchor a single
        /// link across the wire - minting one per cell would leave adjacent cells refusing to
        /// underline together even though every field matched.
        /// </summary>
        private static Hyperlink[] RebuildHyperlinks(List<TerminalHyperlinkEntry> entries)
        {
            if (entries.Count == 0)
            {
                return [];
            }

            var links = new Hyperlink[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                TerminalHyperlinkEntry entry = entries[i];
                links[i] = new Hyperlink(entry.Uri, entry.Id);
            }

            return links;
        }

        // ── shared plumbing ──────────────────────────────────────────────────────

        private static string EncodeCells(TerminalCell[] cells)
        {
            return Convert.ToBase64String(MemoryMarshal.AsBytes(new ReadOnlySpan<TerminalCell>(cells)));
        }

        /// <summary>
        /// Folds one row's side tables into the flat <c>row * cols + col</c> dictionaries, in
        /// ascending column order. The order matters: <see cref="SmallMap{T}"/> enumerates in
        /// insertion order, so emitting it as-is would make a re-export of an imported buffer
        /// order its keys differently from the original for no semantic reason.
        /// </summary>
        private static void AppendSideTables(
            SmallMap<string>? extendedText,
            SmallMap<Hyperlink>? hyperlinks,
            int rowBase,
            int cols,
            HyperlinkTableBuilder links,
            ref Dictionary<int, string>? extendedOut,
            ref Dictionary<int, int>? linkOut)
        {
            if (extendedText is { Count: > 0 })
            {
                foreach (KeyValuePair<int, string> entry in SortedEntries(extendedText))
                {
                    if ((uint)entry.Key >= (uint)cols) continue;
                    (extendedOut ??= [])[rowBase + entry.Key] = entry.Value;
                }
            }

            if (hyperlinks is { Count: > 0 })
            {
                foreach (KeyValuePair<int, Hyperlink> entry in SortedEntries(hyperlinks))
                {
                    if ((uint)entry.Key >= (uint)cols) continue;
                    (linkOut ??= [])[rowBase + entry.Key] = links.IndexOf(entry.Value);
                }
            }
        }

        private static List<KeyValuePair<int, T>> SortedEntries<T>(SmallMap<T> map) where T : class
        {
            var entries = new List<KeyValuePair<int, T>>(map.Count);
            map.ForEach((col, value) => entries.Add(new KeyValuePair<int, T>(col, value)));
            entries.Sort(static (x, y) => x.Key.CompareTo(y.Key));
            return entries;
        }

        private static bool TrySplitKey(int key, int sourceCols, int rowLimit, int colLimit, out int row, out int col)
        {
            row = 0;
            col = 0;
            if (key < 0 || sourceCols <= 0)
            {
                return false;
            }

            row = key / sourceCols;
            col = key % sourceCols;
            return row < rowLimit && col < colLimit;
        }

        private static List<KeyValuePair<int, T>>?[] GroupByRow<T>(
            Dictionary<int, T>? flat, int cols, int rowCount)
        {
            var buckets = new List<KeyValuePair<int, T>>?[Math.Max(0, rowCount)];
            if (flat is null || flat.Count == 0 || cols <= 0)
            {
                return buckets;
            }

            foreach (KeyValuePair<int, T> kv in flat)
            {
                if (kv.Key < 0) continue;
                int row = kv.Key / cols;
                if ((uint)row >= (uint)buckets.Length) continue;
                (buckets[row] ??= []).Add(new KeyValuePair<int, T>(kv.Key % cols, kv.Value));
            }

            return buckets;
        }
    }
}
