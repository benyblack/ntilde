using Avalonia;
using Avalonia.Input;
using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// Pointer input: press, move and release for selection, mouse reports to the session, and converting a pointer
    /// position to a cell.
    /// </summary>
    public partial class TerminalView
    {
        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            bool leftPressed = point.Properties.IsLeftButtonPressed;

            // Check if application has enabled mouse reporting
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                TerminalMouseButton button = GetPressedMouseButton(point.Properties);
                if (button != TerminalMouseButton.None)
                {
                    HandleMousePressAt(point.Position, button, e.KeyModifiers);
                    e.Handled = true;
                    return;
                }
            }

            if (leftPressed)
            {
                if (TryActivateLinkAt(point.Position, e.KeyModifiers))
                {
                    e.Handled = true;
                    return;
                }

                // Normal mode: Handle selection
                var (row, col) = ScreenToTerminal(point.Position);

                // Check for double/triple-click
                if (e.ClickCount == 2)
                {
                    // Double-click: Select word
                    SelectWord(row, col);
                    _isSelecting = false; // Don't start drag selection
                }
                else if (e.ClickCount >= 3)
                {
                    // Triple-click: Select line
                    SelectLine(row);
                    _isSelecting = false; // Don't start drag selection
                }
                else
                {
                    // Single click: Start selection
                    _selection.Start = (row, col);
                    _selection.End = (row, col);
                    _selection.IsActive = true;
                    _isSelecting = true;
                }

                InvalidateVisual();
            }
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);

            // Forward motion events if mouse reporting is active
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                var point = e.GetCurrentPoint(this);
                TerminalMouseButton button = GetPressedMouseButton(point.Properties);
                HandleMouseMoveAt(point.Position, button, e.KeyModifiers);

                e.Handled = true;
                return;
            }

            if (!_isSelecting)
            {
                UpdateHoveredLink(e.GetCurrentPoint(this).Position);
            }

            if (_isSelecting)
            {
                var point = e.GetCurrentPoint(this);
                var (absRow, col) = ScreenToTerminal(point.Position);

                // Update selection end
                _selection.End = (absRow, col);

                // Auto-scroll detection
                double zoneSize = _metrics.CellHeight * 2; // Drag within top/bottom 2 lines
                if (point.Position.Y < zoneSize)
                {
                    // Near Top -> Scroll Up (Increase Offset)
                    StartAutoScroll(-1);
                }
                else if (point.Position.Y > Bounds.Height - zoneSize)
                {
                    // Near Bottom -> Scroll Down (Decrease Offset)
                    StartAutoScroll(1);
                }
                else
                {
                    StopAutoScroll();
                }

                InvalidateVisual();
            }
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);

            // Forward release events if mouse reporting is active
            if (_buffer != null && _buffer.IsMouseReportingActive())
            {
                var point = e.GetCurrentPoint(this);
                var (reportColumn, reportRow) = ToMouseReportCell(point.Position);
                SendMouseEvent(new TerminalMouseEvent(
                    TerminalMouseEventKind.Release,
                    GetReleasedMouseButton(e),
                    reportColumn,
                    reportRow,
                    e.KeyModifiers));
                e.Handled = true;
                return;
            }

            if (_isSelecting)
            {
                _isSelecting = false;
                StopAutoScroll();

                // If start == end, clear selection (was just a click)
                if (_selection.Start == _selection.End)
                {
                    _selection.Clear();
                    InvalidateVisual();
                }
            }
        }

        protected override void OnPointerExited(Avalonia.Input.PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverScanRow = -1;
            ClearHoveredLink();

            // Issue #269: leaving the view means the next OnPointerMoved is a fresh
            // re-entry, not a continuation of whatever cell we last reported. Forget it so
            // re-entry into the same cell reports again instead of being coalesced away.
            ResetMouseMotionTracking();
        }

        // Issue #269: last (row, column) reported for `?1002`/`?1003` motion tracking, used
        // to coalesce to at most one report per cell change instead of flooding the PTY with
        // a report per raw PointerMoved event. Coordinates are 1-based terminal coordinates
        // (matching TerminalMouseEvent), null means "no cell reported yet / tracking reset".
        private (int Row, int Column)? _lastReportedMouseMotionCell;
        private bool _lastMouseMotionAnyEvent;
        private bool _lastMouseMotionButtonEvent;

        /// <summary>
        /// Clears the motion-reporting coalescing state (issue #269) so the next qualifying
        /// pointer move re-reports the current cell rather than being suppressed as a
        /// duplicate of whatever was last sent. Called whenever the "current cell" concept
        /// goes stale: a new buffer is attached (<see cref="SetBuffer"/>), the cell grid is
        /// resized/reflowed, the alternate screen is entered or left
        /// (<see cref="OnScreenSwitched"/>), or the pointer leaves the view
        /// (<see cref="OnPointerExited"/>). Internal (rather than private) so tests can also drive
        /// the reset directly for the sites that are not reachable headlessly (a real
        /// pointer-exited event or layout resize); the SetBuffer and screen-switch sites are
        /// covered through their real call paths.
        /// </summary>
        internal void ResetMouseMotionTracking()
        {
            _lastReportedMouseMotionCell = null;
        }

        /// <summary>
        /// Core motion-reporting logic for <see cref="OnPointerMoved"/> (issue #269), factored
        /// out so it can be exercised directly in tests without a real Avalonia pointer event.
        /// <paramref name="column"/> and <paramref name="row"/> are 1-based terminal
        /// coordinates, matching <see cref="TerminalMouseEvent"/>.
        ///
        /// Under `?1003` (any-event tracking), motion is reported with no buttons held so
        /// hover-driven TUIs (ratatui/bubbletea) can highlight under the cursor; under `?1002`
        /// (button-event tracking) motion is only reported while a button is held, unchanged
        /// from prior behavior. Either way, reports are coalesced to at most one per distinct
        /// cell: Avalonia can raise many PointerMoved events while the cursor sits over a
        /// single terminal cell, and forwarding every one of them would flood the PTY.
        ///
        /// A mode change (1002 vs 1003, either turning on or off) that is OBSERVED BETWEEN TWO
        /// MOTION EVENTS also resets the tracked cell, so a TUI that toggles tracking while the
        /// pointer sits still still gets its next hover reported. This is a sample of
        /// parser-owned <see cref="ModeState"/> taken on the UI thread, not a subscription: a
        /// flip that both begins and ends between two <see cref="OnPointerMoved"/> calls (e.g.
        /// `?1003l` ... `?1003h` inside one redraw) reads as "unchanged" and its first
        /// post-reinit hover in the same cell is still coalesced away. The explicit reset sites
        /// (buffer attach, resize, screen switch, pointer exit) cover the transitions that
        /// actually move content under the pointer; see <see cref="ResetMouseMotionTracking"/>.
        /// </summary>
        internal void HandleMouseMoveCore(TerminalMouseButton button, int column, int row, KeyModifiers modifiers)
        {
            if (_buffer == null) return;

            // ModeState's mouse booleans are written on the PTY reader thread (the parser runs
            // there before output is posted to the dispatcher) and read here on the UI thread
            // without synchronization. Single bool reads cannot tear, and observing a mode one
            // event late only delays a report by one motion event, so no lock is warranted - but
            // it is why the mode-flip detection below is a best-effort sample, not a guarantee.
            ModeState modes = _buffer.Modes;
            bool anyEvent = modes.MouseModeAnyEvent;
            bool buttonEvent = modes.MouseModeButtonEvent;

            if (anyEvent != _lastMouseMotionAnyEvent || buttonEvent != _lastMouseMotionButtonEvent)
            {
                _lastMouseMotionAnyEvent = anyEvent;
                _lastMouseMotionButtonEvent = buttonEvent;
                _lastReportedMouseMotionCell = null;
            }

            bool buttonHeld = button != TerminalMouseButton.None;
            bool wantsReport = anyEvent || (buttonEvent && buttonHeld);
            if (!wantsReport)
            {
                // Neither tracking mode wants this motion. Don't leave a stale cell behind,
                // so re-enabling reporting later doesn't spuriously suppress its first report.
                _lastReportedMouseMotionCell = null;
                return;
            }

            var cell = (row, column);
            if (_lastReportedMouseMotionCell == cell)
            {
                return;
            }

            // Only remember the cell if a report actually went out. A pointer move that lands
            // between SetBuffer and SetSession (no session yet) sends nothing, and recording it
            // anyway would suppress the first real hover in that cell once the session attaches.
            if (SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Move, button, column, row, modifiers)))
            {
                _lastReportedMouseMotionCell = cell;
            }
        }

        /// <summary>
        /// Sends a button-press report for a view-local pointer <paramref name="position"/>
        /// (issue #269 review). Shares <see cref="ToMouseReportCell"/> with the motion, release
        /// and wheel paths so all four report the same viewport-relative coordinate space.
        /// Internal so tests can drive the real press path without a constructible Avalonia
        /// <c>PointerPressedEventArgs</c>.
        /// </summary>
        internal void HandleMousePressAt(Point position, TerminalMouseButton button, KeyModifiers modifiers)
        {
            var (column, row) = ToMouseReportCell(position);
            SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Press, button, column, row, modifiers));
        }

        /// <summary>
        /// Motion path entry point taking a view-local pointer <paramref name="position"/>: turns
        /// it into viewport-relative wire coordinates and hands off to
        /// <see cref="HandleMouseMoveCore"/>. This is what <see cref="OnPointerMoved"/> calls, so
        /// tests driving it exercise the production coordinate conversion rather than a copy.
        /// </summary>
        internal void HandleMouseMoveAt(Point position, TerminalMouseButton button, KeyModifiers modifiers)
        {
            var (column, row) = ToMouseReportCell(position);
            HandleMouseMoveCore(button, column, row, modifiers);
        }

        /// <summary>
        /// Encodes and sends a mouse report. Returns <c>true</c> only when bytes were actually
        /// handed to the session, so callers that track "what we last reported" can avoid
        /// recording a report that never happened.
        /// </summary>
        private bool SendMouseEvent(TerminalMouseEvent mouseEvent)
        {
            if (_session == null || _buffer == null) return false;

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(_buffer.Modes, mouseEvent);
            if (sequence != null)
            {
                _session.SendInput(sequence);
                return true;
            }

            return false;
        }

        private static TerminalMouseButton GetPressedMouseButton(PointerPointProperties properties)
        {
            if (properties.IsLeftButtonPressed) return TerminalMouseButton.Left;
            if (properties.IsMiddleButtonPressed) return TerminalMouseButton.Middle;
            if (properties.IsRightButtonPressed) return TerminalMouseButton.Right;
            return TerminalMouseButton.None;
        }

        private static TerminalMouseButton GetReleasedMouseButton(PointerReleasedEventArgs e)
        {
            return e.InitialPressMouseButton switch
            {
                MouseButton.Left => TerminalMouseButton.Left,
                MouseButton.Middle => TerminalMouseButton.Middle,
                MouseButton.Right => TerminalMouseButton.Right,
                _ => TerminalMouseButton.None
            };
        }

        /// <summary>
        /// Converts a view-local pointer position into the 1-based <c>(column, row)</c> pair that
        /// goes on the wire in an xterm mouse report.
        ///
        /// Mouse coordinates are VIEWPORT-relative: row 1 is the top line currently on screen no
        /// matter how much scrollback sits behind it, and the row never exceeds the screen height.
        /// This is deliberately NOT <see cref="ScreenToTerminal"/>, which returns a
        /// scrollback-ABSOLUTE row - the correct space for selection anchors, word/line selection
        /// and hyperlink hit-testing, because those index into scrollback via
        /// <c>GetCellAbsolute</c>/<c>GetHyperlinkAbsolute</c> and must stay pinned to their content
        /// while the view scrolls. Both contracts are needed; they must not be interchanged.
        ///
        /// Issue #269 review fix: every mouse-report call site used to send the absolute row, so a
        /// session with 500 lines of scrollback reported row 501 for the top-left cell (only the
        /// alt screen was accidentally correct, since there TotalLines == Rows). That also defeated
        /// motion coalescing - the dedup key drifted under a physically stationary pointer as
        /// output scrolled - and pushed rows past the legacy encoding's 223-coordinate ceiling.
        /// </summary>
        private (int Column, int Row) ToMouseReportCell(Point position)
        {
            if (_buffer == null || _metrics.CellWidth <= 0 || _metrics.CellHeight <= 0)
            {
                return (1, 1);
            }

            int col = Math.Clamp((int)(position.X / _metrics.CellWidth), 0, _buffer.Cols - 1);
            int visualRow = Math.Clamp((int)(position.Y / _metrics.CellHeight), 0, _buffer.Rows - 1);
            return (col + 1, visualRow + 1);
        }

        /// <summary>
        /// Converts screen coordinates to terminal ABSOLUTE row/col (scrollback-relative: row 0 is
        /// the oldest line still in history). Used by selection and link hit-testing, which need to
        /// stay attached to content across scrolling. Mouse REPORTING must not use this - see
        /// <see cref="ToMouseReportCell"/>.
        /// </summary>
        private (int Row, int Col) ScreenToTerminal(Point position)
        {
            if (_buffer == null) return (0, 0);

            int col = (int)(position.X / _metrics.CellWidth);
            int visualRow = (int)(position.Y / _metrics.CellHeight);

            // Clamp visual row first
            visualRow = Math.Clamp(visualRow, 0, _buffer.Rows - 1);

            // Convert to Absolute Row
            // Visible Top Index = Total - Rows - Offset
            int totalLines = _buffer.TotalLines;
            int displayStart = Math.Max(0, totalLines - _buffer.Rows - _scrollOffset);
            int absRow = displayStart + visualRow;

            // Clamp columns
            col = Math.Clamp(col, 0, _buffer.Cols - 1);

            // AbsRow shouldn't need clamping if logic correct, but safety:
            absRow = Math.Clamp(absRow, 0, totalLines - 1);

            return (absRow, col);
        }
    }
}
