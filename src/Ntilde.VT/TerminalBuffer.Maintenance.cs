using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Ntilde.VT.Storage;
namespace Ntilde.VT
{
    public partial class TerminalBuffer
    {
        // Handles of images removed from _images, awaiting disposal, stamped with the retire
        // time. Removal is synchronous; disposal waits until no snapshot session that could
        // still hold the handle remains: both render pipelines (live view frames and
        // agent-host captures) enter a session for the duration of DrawTerminalInternal, so
        // disposal is exact rather than a duration guess (a capture drawing thousands of
        // images under load can exceed any fixed grace). DisposeImmediately marks entries the
        // owning view may dispose at its very next frame boundary with no additional grace —
        // video-frame replacements, where a grace would retain a two-second backlog of full
        // bitmaps at 30 fps.
        private readonly ConcurrentQueue<(object Handle, long Tick, bool DisposeImmediately, long RetirementToken)> _retiredImageHandles = new();

        // Sessions keyed by id, valued by start tick. A retire entry may only be disposed
        // once every session that started at or before its retire tick has ended: those are
        // exactly the sessions whose snapshot may have copied the handle before the prune.
        private readonly ConcurrentDictionary<int, long> _activeSnapshotSessions = new();
        private int _nextSnapshotSessionId;

        public void AddImage(TerminalImage image)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                RequireImageHandleAdmission(image.ImageHandle);
                // Tag ownership: alt-screen images use viewport-relative CellY, main-screen
                // images absolute rows. Lets erase/rebase paths tell the two apart even
                // though both live in the same list.
                image.IsAltScreenImage = _isAltScreen;
                _images.Add(image);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        /// <summary>
        /// Adds a kitty graphics frame under image number <paramref name="kittyId"/>. Kitty
        /// semantics: a new image transmitted with the same number replaces the previous one —
        /// the wire shape a video-rate frame stream (terminal-browser) relies on. Without the
        /// replacement, every frame at 30 fps would stack another full-size bitmap and scroll
        /// the buffer, at hundreds of MB per minute. Replacement goes through the same
        /// retire-and-deferred-dispose path as pruning, so in-flight snapshots are safe, and it
        /// only matches images owned by the ACTIVE screen: an alt-screen frame reusing an id
        /// must not delete the main screen's hidden image, or leaving the TUI could not
        /// restore it (the same isolation rule every erase/scroll path here follows).
        /// </summary>
        public void AddKittyFrame(TerminalImage image, uint kittyId)
        {
            image.KittyImageId = kittyId;
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                RequireImageHandleAdmission(image.ImageHandle);
                image.IsAltScreenImage = _isAltScreen;
                // Publish the replacement before releasing older placements, including
                // when a host decoder reuses the same opaque handle.
                _images.Add(image);
                for (int i = _images.Count - 2; i >= 0; i--)
                {
                    if (_images[i].KittyImageId == kittyId && _images[i].IsAltScreenImage == _isAltScreen)
                    {
                        // Immediate disposal: at 30 fps even a two-second backlog of replaced
                        // full-size frames retains dozens of bitmaps. The snapshot-session
                        // gate inside the drain is what makes this safe, not a grace.
                        RetireImage(_images[i], disposeImmediately: true);
                        _images.RemoveAt(i);
                    }
                }
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        /// <summary>
        /// Queues a removed image's handle for deferred disposal. The queue holds opaque
        /// objects — VT cannot reference SkiaSharp; the render layer's frame-boundary drain
        /// (<see cref="DrainRetiredImageHandles"/>) owns the actual Dispose.
        /// </summary>
        private void RetireImage(TerminalImage image, bool disposeImmediately = false)
        {
            // Source text belongs to the removed placement, independently of an opaque
            // decoded handle that another placement or retained Kitty asset may share.
            (image.SixelSource ?? (image.ImageHandle as IDeferredSixelImage)?.Source)?.Retire();
            if (IsRetainedKittyHandle(image.ImageHandle) || HasOtherImageReference(image)) return;
            // A headless source handle has no renderer resources or in-flight bitmap users.
            if (image.ImageHandle is SixelSource source) { source.Dispose(); return; }
            QueueImageHandleRetirement(image.ImageHandle, disposeImmediately);
        }

        /// <summary>
        /// Marks the start of a render pass that will snapshot this buffer and draw the
        /// image handles it copies out. Every pipeline drawing images (live view frames,
        /// agent-host captures) must bracket its pass with this and
        /// <see cref="EndSnapshotSession"/>: a retire entry is only disposed once no session
        /// that started at or before its retire tick is still active, which is what makes
        /// disposal exact instead of a duration guess.
        /// </summary>
        public int BeginSnapshotSession()
        {
            int id = System.Threading.Interlocked.Increment(ref _nextSnapshotSessionId);
            _activeSnapshotSessions[id] = Environment.TickCount64;
            return id;
        }

        public void EndSnapshotSession(int sessionId)
        {
            List<IDisposable>? leases;
            lock (_sixelSnapshotGate)
            {
                _activeSnapshotSessions.TryRemove(sessionId, out _);
                _sixelSnapshotLeases.Remove(sessionId, out leases);
            }
            if (leases is not null) foreach (var lease in leases) lease.Dispose();
        }

        private long MinActiveSessionStartTick()
        {
            long min = long.MaxValue;
            foreach (var pair in _activeSnapshotSessions)
            {
                if (pair.Value < min) min = pair.Value;
            }
            return min;
        }

        /// <summary>
        /// Drains retired handles into <paramref name="into"/>. An entry is released only
        /// when BOTH hold: it is flagged for immediate disposal or is older than
        /// <paramref name="retiredBeforeTick"/> (the owning view's frame boundary + grace),
        /// AND no snapshot session started at or before its retire tick is still active (an
        /// in-flight capture of any duration blocks disposal). The queue is FIFO by retire
        /// tick, so the drain stops at the first entry that cannot yet be released.
        /// </summary>
        public bool DrainRetiredImageHandles(List<object> into, long retiredBeforeTick)
        {
            if (_retiredImageHandles.IsEmpty) return false;
            lock (_imageHandleGate)
            {
                long minActiveSessionStart = MinActiveSessionStartTick();
                bool drained = false;
                while (_retiredImageHandles.TryPeek(out var entry))
                {
                    // Canceled generations may be discarded even while an old snapshot
                    // delays the current generation. Never reuse their earlier retirement tick.
                    if (!IsCurrentImageRetirement(entry.Handle, entry.RetirementToken))
                    {
                        _retiredImageHandles.TryDequeue(out _);
                        continue;
                    }
                    if ((!entry.DisposeImmediately && entry.Tick > retiredBeforeTick) || entry.Tick >= minActiveSessionStart) break;
                    if (!_retiredImageHandles.TryDequeue(out entry)) continue;
                    if (!TryCompleteImageRetirement(entry.Handle, entry.RetirementToken)) continue;
                    into.Add(entry.Handle);
                    drained = true;
                }
                return drained;
            }
        }

        public void ClearImages()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ClearImagePlacements();
                ClearRetainedKittyImages();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        /// <summary>
        /// Clears both the visible screen and the scrollback history.
        /// Used by RIS (full reset) and explicit user-invoked "clear buffer" actions.
        /// VT erase sequences must NOT call this: ED 2 (CSI 2 J) only clears the
        /// screen (<see cref="ClearScreen"/>) and ED 3 (CSI 3 J) only clears the
        /// scrollback (<see cref="ClearScrollbackHistory"/>).
        /// </summary>
        public void Clear(bool resetCursor = true)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                _scrollback.Clear();
                ClearImagePlacements(); // full clear: scrollback-anchored images lose their anchor too
                ClearRetainedKittyImages();
                ClearScreenInternal(resetCursor);

                // Attribute reset belongs to the full-clear path only (RIS / explicit UI
                // clear). ED 2 must leave SGR state untouched — a TUI that enables
                // bold/inverse and repaints via CSI 2 J keeps its attributes.
                IsInverse = false;
                IsBold = false;
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        /// <summary>
        /// Erases the visible screen only (ED 2 semantics). Scrollback history is preserved.
        /// </summary>
        public void ClearScreen(bool resetCursor = false)
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ClearScreenInternal(resetCursor);
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        /// <summary>
        /// Clears the scrollback history only (ED 3 / "Erase Saved Lines" semantics).
        /// The visible screen is left untouched.
        /// </summary>
        public void ClearScrollbackHistory()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int clearedRows = _scrollback.Count;
                _scrollback.Clear();

                // Main-screen image CellY is absolute (scrollback + viewport), so removing
                // scrollback rows shifts every main-screen anchor down — same convention as
                // the eviction shift in ScrollUpInternal. Images now entirely above row 0
                // lived in the cleared history and are pruned. This must run even while the
                // alt screen is active (ED 3 still clears main-screen history); alt-screen
                // images are viewport-relative and skipped via their ownership tag.
                if (clearedRows > 0)
                {
                    for (int i = _images.Count - 1; i >= 0; i--)
                    {
                        var img = _images[i];
                        if (img.IsAltScreenImage) continue;

                        img.CellY -= clearedRows;
                        if (img.CellY + img.CellHeight <= 0)
                        {
                            RetireImage(img);
                            _images.RemoveAt(i);
                        }
                    }
                }
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }

            Invalidate();
        }

        private void ClearScreenInternal(bool resetCursor)
        {
            EraseTextSizingRange(0, Cols, TextSizingAbsoluteRow(0), TextSizingAbsoluteRow(Rows));
            // Remove images intersecting the visible screen of the ACTIVE buffer only.
            // On the main screen CellY is absolute (scrollback + viewport; see
            // ScrollUpInternal/InsertLines), so images living entirely in scrollback are
            // preserved — ED 2 must not touch history. Images belonging to the inactive
            // screen are untouched. An image straddling the scrollback/viewport boundary
            // is removed entirely, since a partial erase isn't representable.
            int viewportTopAbs = _isAltScreen ? 0 : _scrollback.Count;
            for (int i = _images.Count - 1; i >= 0; i--)
            {
                var img = _images[i];
                if (img.IsAltScreenImage != _isAltScreen) continue;

                if (img.CellY + img.CellHeight > viewportTopAbs)
                {
                    RetireImage(img);
                    _images.RemoveAt(i);
                }
            }

            // CRITICAL: Erase cells IN-PLACE rather than replacing row objects.
            // TUI apps like Yazi rely on partial redraws — they only redraw rows that changed.
            // If we replace all row objects (new IDs), Yazi's next frame skips unchanged rows,
            // leaving blank row objects on screen instead of re-drawn content.
            for (int i = 0; i < Rows; i++)
            {
                ClearRowInternal(i);
            }

            if (resetCursor)
            {
                _cursorCol = 0;
                _cursorRow = 0;
            }

            // Mouse modes should only change via DEC private mode sequences,
            // not from screen clearing operations (htop clears screen after enabling mouse)
        }

        public void ScreenAlignmentPattern()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                EraseTextSizingRange(0, Cols, TextSizingAbsoluteRow(0), TextSizingAbsoluteRow(Rows));
                // DECALN: Fill screen with 'E'
                var cell = new TerminalCell('E', Theme.Foreground, Theme.Background, false, false, true, true);

                for (int r = 0; r < Rows; r++)
                {
                    // Ensure we have a valid row
                    if (_viewport[r] == null) _viewport[r] = new TerminalRow(Cols, Theme.Foreground, Theme.Background);

                    var row = _viewport[r];
                    for (int c = 0; c < Cols; c++)
                    {
                        row.Cells[c] = cell;
                    }
                    row.TouchRevision();
                }

                // Reset cursor to home
                _cursorCol = 0;
                _cursorRow = 0;
                _isPendingWrap = false; // Reset wrap state
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        private void ResetCursorPresentationNoLock()
        {
            Modes.CursorStyle = CursorStyle.Underline;
            Modes.IsCursorBlinkEnabled = true;
            Modes.CursorStyleIsDefault = true;
        }

        /// <summary>
        /// DECSTR, following xterm's soft-reset defaults. Content, current position, active screen,
        /// tab stops and reporting modes survive; only the active DECSC bank is reinitialized.
        /// modifyOtherKeys is the exception among the input modes: xterm resets it here too.
        /// </summary>
        public void SoftReset()
        {
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                int row = _cursorRow;
                int col = _cursorCol;
                ResetCursorStateToDefaultsNoLock();
                _cursorRow = row;
                _cursorCol = col;
                ScrollTop = 0;
                ScrollBottom = Rows - 1;
                Modes.IsOriginMode = false;
                Modes.IsAutoWrapMode = true;
                Modes.IsApplicationCursorKeys = false;
                Modes.IsApplicationKeypad = false;
                Modes.IsInsertMode = false;
                Modes.IsCursorVisible = true;
                // Unlike the kitty stacks and the other reporting modes, xterm's soft reset
                // restores the key-modifier resources (modify_now = modify_1st in ReallyReset),
                // so DECSTR returns modifyOtherKeys to 0 on both screens.
                Modes.ModifyOtherKeys.Reset();
                ResetCursorPresentationNoLock();
                ResetSavedCursorStateToDefaultsNoLock(_isAltScreen ? _savedCursors.Alt : _savedCursors.Main);
            }
            finally { ExitWriteLockIfNeeded(Lock, lockTaken); }
            Invalidate();
        }

        public void Reset()
        {
            // Full Reset (RIS): screen selection, pen reset and clearing are one write cut.
            bool lockTaken = EnterWriteLockIfNeeded();
            try
            {
                ScrollTop = 0;
                ScrollBottom = Rows - 1;
                Modes.IsSixelDisplayMode = false;
                // DECSCNM returns to normal video on RIS (xterm); DECSTR deliberately keeps it.
                if (Modes.IsReverseVideo)
                {
                    Modes.IsReverseVideo = false;
                    MarkRenderThemeChangedNoLock();
                }
                Modes.IsAutoWrapMode = true;
                Modes.IsApplicationCursorKeys = false;
                Modes.IsApplicationKeypad = false;
                Modes.IsClipboardPasteMode = false;
                Modes.IsCursorVisible = true;
                ResetCursorPresentationNoLock();

                // Reset Mouse Modes
                Modes.MouseModeX10PressOnly = false;
                Modes.MouseModeX10 = false;
                Modes.MouseModeButtonEvent = false;
                Modes.MouseModeAnyEvent = false;
                Modes.MouseModeSGR = false;

                // ?1007 returns to the host's configured default, as xterm's RIS restores the
                // alternateScroll resource. DECSTR leaves it alone (xterm's soft reset list).
                Modes.AlternateScrollOverride = null;

                _restoreMainCursorOnAltExit = false;
                SwitchToMainScreen(restoreSavedCursorIfArmed: false);

                // The SGR/cursor reset runs AFTER the screen switch on purpose. Leaving the alt
                // screen restores the main screen's saved style, which is whatever was live when
                // the program entered the alt screen - usually an explicit SGR color. Resetting
                // before the switch let that restore undo RIS, and the resurrected explicit
                // state then made every later theme switch skip the text written after it.
                ResetCursorStateToDefaultsNoLock();

                // Same reason, one step removed: RIS reinitializes the saved slots too, or a
                // later DECRC (ESC 8) or alt-screen exit hands the pre-RIS style straight back.
                ResetSavedCursorStateToDefaultsNoLock(_savedCursors.Main);
                ResetSavedCursorStateToDefaultsNoLock(_savedCursors.Alt);
                ResetSavedCursorStateToDefaultsNoLock(_screenCursorStates.Main);
                ResetSavedCursorStateToDefaultsNoLock(_screenCursorStates.Alt);

                ImportRuntimeColors(null);

                // Clear after returning to main and resetting the pen. Clearing only the
                // formerly active alternate screen left main text visible; using the old
                // pen also left explicit colors on otherwise blank cells.
                Clear(true);
                // Main is active now; a width resize can leave _mainScreen stale.
                foreach (var row in _viewport) row.IsWrapped = false;
                ResetAlternateScreenRowsNoLock();
                if (_textSizingBlocks != null) PruneTextSizingStorage();

                // Kitty keyboard protocol: RIS clears both per-screen flag stacks so the
                // terminal comes back up in legacy key encoding.
                Modes.KittyKeyboard.Reset();
                // xterm RIS restores modifyOtherKeys' initial value (0) on both screens.
                Modes.ModifyOtherKeys.Reset();

                ResetTabStopsToDefaultsNoLock();
            }
            finally
            {
                ExitWriteLockIfNeeded(Lock, lockTaken);
            }
            Invalidate();
        }

        private void ResetAlternateScreenRowsNoLock()
        {
            _textSizingPruneRequired = true;
            var blank = new TerminalCell(' ', Theme.Foreground, Theme.Background, false, false, true, true);
            // The inactive array may still have its pre-resize shape. Clear every retained
            // row in place; normal re-entry remains responsible for shape normalization.
            foreach (var row in _altScreen)
            {
                row.IsWrapped = false;
                row.WrittenColumns = 0;
                Array.Fill(row.Cells, blank);
                row.ClearExtendedText();
                row.ClearHyperlinks();
                row.ClearUnderlines();
                row.ClearTextSizing();
                row.TouchRevision();
            }
        }

        public void UpdateThemeColors(TerminalTheme oldTheme)
        {
            AcquireWriteLock();
            try
            {
                // Invalidate render-side caches keyed by the theme epoch (paged-scrollback
                // snapshots and everything derived from them). Must happen under the write lock
                // so a snapshot captured after this call always observes the new epoch.
                _renderThemeEpoch++;
                _runtimeTheme = null;

                // Sync current buffer defaults to new theme. SyncDefault*ToTheme keeps the
                // default flag - a bare CurrentForeground assignment would clear the very flag
                // the guard just tested, turning the live SGR state explicit and freezing every
                // subsequent write into this theme's colors (and the guard would then be false
                // on the next switch, so the state would stay pinned to the old theme forever).
                if (IsDefaultForeground) SyncDefaultForegroundToTheme();
                if (IsDefaultBackground) SyncDefaultBackgroundToTheme();
                SyncThemeDefaultsInCursorStateNoLock(_savedCursors.Main);
                SyncThemeDefaultsInCursorStateNoLock(_savedCursors.Alt);
                SyncThemeDefaultsInCursorStateNoLock(_screenCursorStates.Main);
                SyncThemeDefaultsInCursorStateNoLock(_screenCursorStates.Alt);

                // NOTE: deliberately no value-based adoption here. Rewriting cells whose color
                // happens to equal the old default would also capture explicitly-requested
                // truecolors (programs query OSC 11 and paint to match the terminal), silently
                // converting them to theme-following defaults. Explicit stays explicit; only
                // flagged default cells migrate.
                void UpdateCell(ref TerminalCell cell)
                {
                    // Preserve palette/default flags across theme switches.
                    // Using Foreground/Background setters here would clear those flags.
                    short fgIdx = cell.FgIndex;
                    short bgIdx = cell.BgIndex;

                    // Indices take precedence
                    if (fgIdx >= 0 && fgIdx <= 15)
                    {
                        cell.IsPaletteForeground = true;
                        cell.IsDefaultForeground = false;
                        cell.Fg = (uint)fgIdx;
                    }
                    else if (cell.IsDefaultForeground)
                    {
                        cell.IsPaletteForeground = false;
                        cell.IsDefaultForeground = true;
                        cell.Fg = Theme.Foreground.ToUint();
                    }

                    if (bgIdx >= 0 && bgIdx <= 15)
                    {
                        cell.IsPaletteBackground = true;
                        cell.IsDefaultBackground = false;
                        cell.Bg = (uint)bgIdx;
                    }
                    else if (cell.IsDefaultBackground)
                    {
                        cell.IsPaletteBackground = false;
                        cell.IsDefaultBackground = true;
                        cell.Bg = Theme.Background.ToUint();
                    }

                    cell.IsDirty = true;
                }

                for (int r = 0; r < Rows; r++)
                {
                    for (int c = 0; c < Cols; c++)
                    {
                        UpdateCell(ref _viewport[r].Cells[c]);
                    }
                    _viewport[r].TouchRevision();
                }

                _scrollback.UpdateThemeDefaults(oldTheme, Theme);

                foreach (var row in _mainScreen) row.TouchRevision();
                foreach (var row in _altScreen) row.TouchRevision();
            }
            finally
            {
                Lock.ExitWriteLock();
            }
            Invalidate();
        }

        /// <summary>
        /// Checks if any mouse reporting mode is active.
        /// </summary>
        public bool IsMouseReportingActive()
        {
            return Modes.MouseModeX10PressOnly || Modes.MouseModeX10 || Modes.MouseModeButtonEvent || Modes.MouseModeAnyEvent;
        }
    }
}
