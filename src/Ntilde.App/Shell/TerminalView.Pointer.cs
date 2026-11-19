using Avalonia;
using Avalonia.Input;
using Ntilde.Pty;
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
            if (!CanApplyGeneratedInput) { e.Handled = true; return; }
            base.OnPointerPressed(e);

            var point = e.GetCurrentPoint(this);
            bool leftPressed = point.Properties.IsLeftButtonPressed;

            // Check if application has enabled mouse reporting
            bool reporting = _buffer != null && _buffer.IsMouseReportingActive();
            if (!reporting && _generatedInputInProgress) { e.Handled = true; return; }
            if (reporting)
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
            if (!CanApplyGeneratedInput) { e.Handled = true; return; }
            base.OnPointerMoved(e);

            // Forward motion events if mouse reporting is active
            bool reporting = _buffer != null && _buffer.IsMouseReportingActive();
            if (!reporting && _generatedInputInProgress) { e.Handled = true; return; }
            if (reporting)
            {
                // The motion is the program's now, so the hover never updates: a link hovered before would keep its
                // underline and its tip (CodeRabbit on #634).
                ClearHoveredLink();
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
            if (!CanApplyGeneratedInput) { e.Handled = true; return; }
            base.OnPointerReleased(e);

            // Forward release events if mouse reporting is active
            bool reporting = _buffer != null && _buffer.IsMouseReportingActive();
            if (!reporting && _generatedInputInProgress) { e.Handled = true; return; }
            if (reporting)
            {
                var point = e.GetCurrentPoint(this);
                var (reportColumn, reportRow) = ToMouseReportCell(point.Position);
                var (reportPixelX, reportPixelY) = ToMouseReportPixel(point.Position);
                SendMouseEvent(new TerminalMouseEvent(
                    TerminalMouseEventKind.Release,
                    GetReleasedMouseButton(e),
                    reportColumn,
                    reportRow,
                    e.KeyModifiers,
                    reportPixelX,
                    reportPixelY));
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
        // Under SGR-Pixels (?1016) the key is the (1-based) pixel position instead: xterm compares the pixel offsets
        // it reports, so every pixel change is a new report and the same pixel is still coalesced.
        private (int Row, int Column)? _lastReportedMouseMotionCell;
        private bool _lastMouseMotionAnyEvent;
        private bool _lastMouseMotionButtonEvent;
        private bool _lastMouseMotionPixel;

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
            => HandleMouseMoveCore(button, column, row, modifiers, 0, 0);

        /// <summary>
        /// <see cref="HandleMouseMoveCore(TerminalMouseButton, int, int, KeyModifiers)"/> with the one-based pixel position
        /// <paramref name="pixelX"/>, <paramref name="pixelY"/> (see <see cref="ToMouseReportPixel(Point)"/>). The pixel
        /// position is only the coalescing key and the report's coordinates while SGR-Pixels (?1016) is the active encoding.
        /// </summary>
        internal void HandleMouseMoveCore(TerminalMouseButton button, int column, int row, KeyModifiers modifiers, int pixelX, int pixelY)
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

            bool pixelMode = modes.MouseModePixel;
            if (anyEvent != _lastMouseMotionAnyEvent || buttonEvent != _lastMouseMotionButtonEvent || pixelMode != _lastMouseMotionPixel)
            {
                _lastMouseMotionAnyEvent = anyEvent;
                _lastMouseMotionButtonEvent = buttonEvent;
                _lastMouseMotionPixel = pixelMode;
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

            var cell = pixelMode ? (pixelY, pixelX) : (row, column);
            if (_lastReportedMouseMotionCell == cell)
            {
                return;
            }

            // Only remember the cell if a report actually went out. A pointer move that lands
            // between SetBuffer and SetSession (no session yet) sends nothing, and recording it
            // anyway would suppress the first real hover in that cell once the session attaches.
            if (SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Move, button, column, row, modifiers, pixelX, pixelY)))
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
            var (pixelX, pixelY) = ToMouseReportPixel(position);
            SendMouseEvent(new TerminalMouseEvent(TerminalMouseEventKind.Press, button, column, row, modifiers, pixelX, pixelY));
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
            var (pixelX, pixelY) = ToMouseReportPixel(position);
            HandleMouseMoveCore(button, column, row, modifiers, pixelX, pixelY);
        }

        /// <summary>
        /// Encodes and sends a mouse report. Returns <c>true</c> only when bytes were actually
        /// handed to the session, so callers that track "what we last reported" can avoid
        /// recording a report that never happened.
        /// </summary>
        private bool SendMouseEvent(TerminalMouseEvent mouseEvent)
        {
            if (_session == null || _buffer == null) return false;

            string? sequence = TerminalInputModeEncoder.EncodeMouseEvent(_buffer.Modes, mouseEvent, out bool legacyBytes);
            return sequence != null && SendEncodedMouseReport(sequence, legacyBytes);
        }

        // The encoder and transport selection are separate stages; mode changes between
        // them must not change the format of the already encoded frame.
        // legacyBytes: the frame is the plain X10 form, whose characters are octets. A ?1005 frame also starts with
        // CSI M but is text (its code points above U+007F are sent UTF-8-encoded), so it takes the ordinary path.
        internal bool SendEncodedMouseReport(string sequence, bool legacyBytes = true)
        {
            if (_session == null || _buffer == null) return false;
            if (legacyBytes && sequence.StartsWith("\u001b[M", StringComparison.Ordinal))
            {
                if (_session is ITerminalByteInput { SupportsByteInput: true } bytes)
                {
                    bool submitted = bytes.TrySendInputBytes(System.Text.Encoding.Latin1.GetBytes(sequence));
                    if (submitted) NoteGeneratedInputSubmitted();
                    return submitted;
                }
                // Text-only/older peers carry ASCII exactly. Do not UTF-8-expand legacy
                // coordinate bytes above 127 into a different mouse report.
                if (sequence.AsSpan().ContainsAnyExceptInRange('\0', '\u007f')) return false;
            }
            _session.SendInput(sequence);
            NoteGeneratedInputSubmitted();
            return true;
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
        internal (int Column, int Row) ToMouseReportCell(Point position)
        {
            if (_buffer == null || _metrics.CellWidth <= 0 || _metrics.CellHeight <= 0)
            {
                return (1, 1);
            }

            int visualRow = Math.Clamp((int)(position.Y / _metrics.CellHeight), 0, _buffer.Rows - 1);
            int displayStart = Math.Max(0, _buffer.TotalLines - _buffer.Rows - _scrollOffset);
            int col = ColumnAtX(position.X, displayStart + visualRow);
            return (col + 1, visualRow + 1);
        }

        /// <summary>
        /// The pointer position SGR-Pixels (?1016) reports: the one-based pixel position inside the text area, in the same device
        /// pixels <c>CSI 14 t</c>, <c>CSI 16 t</c> and Sixel placement use (the logical position times
        /// <see cref="EffectiveRenderScaling"/>). xterm sends the zero-based offset plus one and does not clamp; the pointer
        /// here is clamped to the grid, as <see cref="ToMouseReportCell"/> clamps the cell, so both stay in one coordinate space.
        /// </summary>
        internal (int X, int Y) ToMouseReportPixel(Point position)
        {
            if (_buffer == null) return (1, 1);
            var (scaleX, scaleY) = MousePixelScales();
            return ToMouseReportPixel(position, scaleX, scaleY, _metrics.CellWidth, _metrics.CellHeight, _buffer.Cols, _buffer.Rows);
        }

        /// <summary>
        /// The cell size, in pixels, the pane's session reports through <c>CSI 14 t</c>, <c>CSI 16 t</c> and image placement, when that is not this view's own device-pixel cell. A mux client has it: the
        /// daemon holds one size for the session (the latest client wins) and every client's parser mirrors it, while this view's render scaling and font metrics are its own. Null for a direct pane, whose
        /// parser is set from this view. Read on the UI thread; the value it returns is an immutable record the delivery thread replaces.
        /// </summary>
        internal Func<SixelCellSize?>? SessionPixelCellProvider { get; set; }

        /// <summary>
        /// Device pixels per logical pixel on each axis for the pointer position SGR-Pixels reports. A direct pane's is its own render scaling. A mux client maps the pointer into the session's reported cell
        /// size, so a frame from any client, whatever its font or display scaling, is in the unit the session's size replies use right now; a position that crosses a cell boundary here crosses it there.
        /// </summary>
        private (double X, double Y) MousePixelScales()
        {
            double scale = double.IsFinite(_cachedRenderScaling) && _cachedRenderScaling > 0 ? _cachedRenderScaling : 1;
            if (SessionPixelCellProvider?.Invoke() is { } cell
                && float.IsFinite(cell.WidthPx) && float.IsFinite(cell.HeightPx) && cell.WidthPx > 0 && cell.HeightPx > 0
                && _metrics.CellWidth > 0 && _metrics.CellHeight > 0)
                // In double, from the float sizes: a float ratio carries the rounding of a float into every pixel and the extent. A cell is never under one pixel, as the parser's ImageCellPixels reports it.
                return (Math.Max(1.0, cell.WidthPx) / (double)_metrics.CellWidth, Math.Max(1.0, cell.HeightPx) / (double)_metrics.CellHeight);
            return (scale, scale);
        }

        /// <summary>The pure conversion behind <see cref="ToMouseReportPixel(Point)"/>: the logical position, the scale and the grid in, the one-based pixel position out.</summary>
        internal static (int X, int Y) ToMouseReportPixel(Point position, double scale, double cellWidth, double cellHeight, int cols, int rows) =>
            ToMouseReportPixel(position, scale, scale, cellWidth, cellHeight, cols, rows);

        /// <summary>As the square-scale form, with the scale of each axis: a mux client's is the session's cell size over its own logical cell.</summary>
        internal static (int X, int Y) ToMouseReportPixel(Point position, double scaleX, double scaleY, double cellWidth, double cellHeight, int cols, int rows)
        {
            if (!TryGetTextAreaPixels(scaleX, scaleY, cellWidth, cellHeight, cols, rows, out scaleX, out scaleY, out int width, out int height)) return (1, 1);
            return (PixelOffset(position.X, scaleX, width) + 1, PixelOffset(position.Y, scaleY, height) + 1);
        }

        private static int PixelOffset(double logical, double scale, int extent)
        {
            double pixels = Math.Floor(logical * scale);
            return double.IsNaN(pixels) ? 0 : (int)Math.Clamp(pixels, 0, extent - 1);
        }

        /// <summary>The text area in whole device pixels (the largest pixel offset is one less), or false for a grid without a measurable size.</summary>
        internal static bool TryGetTextAreaPixels(double scale, double cellWidth, double cellHeight, int cols, int rows,
            out double effectiveScale, out int width, out int height) =>
            TryGetTextAreaPixels(scale, scale, cellWidth, cellHeight, cols, rows, out effectiveScale, out _, out width, out height);

        internal static bool TryGetTextAreaPixels(double scaleX, double scaleY, double cellWidth, double cellHeight, int cols, int rows,
            out double effectiveScaleX, out double effectiveScaleY, out int width, out int height)
        {
            effectiveScaleX = double.IsFinite(scaleX) && scaleX > 0 ? scaleX : 1;
            effectiveScaleY = double.IsFinite(scaleY) && scaleY > 0 ? scaleY : 1;
            width = height = 0;
            if (!double.IsFinite(cellWidth) || !double.IsFinite(cellHeight) || cellWidth <= 0 || cellHeight <= 0 || cols <= 0 || rows <= 0) return false;
            // The extent is what CSI 14 t reports: the float cell size the session holds (SyncLocalSixelCellSize stores it for a direct pane, the daemon for a mux one) times the grid, rounded, so the last
            // pixel the reply counts is also reachable here (flooring would lose it to float error at fractional scales).
            double w = Math.Round(cols * (float)(cellWidth * effectiveScaleX)), h = Math.Round(rows * (float)(cellHeight * effectiveScaleY));
            if (w < 1 || h < 1 || w > int.MaxValue || h > int.MaxValue) return false;
            width = (int)w;
            height = (int)h;
            return true;
        }

        /// <summary>
        /// The logical pointer position at the centre of the device pixel (<paramref name="pixelX"/>, <paramref name="pixelY"/>), zero-based
        /// inside the text area, or false when that pixel lies outside the grid. Centring keeps the floor in
        /// <see cref="ToMouseReportPixel(Point)"/> from landing on a neighbouring pixel through rounding.
        /// </summary>
        internal bool TryGetPixelPosition(int pixelX, int pixelY, out Point position)
        {
            position = default;
            var (requestedX, requestedY) = MousePixelScales();
            if (_buffer == null || !TryGetTextAreaPixels(requestedX, requestedY, _metrics.CellWidth, _metrics.CellHeight, _buffer.Cols, _buffer.Rows,
                    out double scaleX, out double scaleY, out int width, out int height)
                || pixelX < 0 || pixelY < 0 || pixelX >= width || pixelY >= height) return false;
            position = new Point((pixelX + 0.5) / scaleX, (pixelY + 0.5) / scaleY);
            return true;
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

            int visualRow = (int)(position.Y / _metrics.CellHeight);

            // Clamp visual row first
            visualRow = Math.Clamp(visualRow, 0, _buffer.Rows - 1);

            // Convert to Absolute Row
            // Visible Top Index = Total - Rows - Offset
            int totalLines = _buffer.TotalLines;
            int displayStart = Math.Max(0, totalLines - _buffer.Rows - _scrollOffset);
            int absRow = displayStart + visualRow;

            // AbsRow shouldn't need clamping if logic correct, but safety:
            absRow = Math.Clamp(absRow, 0, totalLines - 1);

            return (absRow, ColumnAtX(position.X, absRow));
        }

        /// <summary>
        /// The centre of a one-based viewport cell: the inverse of <see cref="ToMouseReportCell"/>, so on a double-size
        /// line, whose cells are two cells wide, a column is found where it is drawn.
        /// </summary>
        internal bool IsDoubleSizeViewportRow(int row)
        {
            if (_buffer == null || row < 1 || row > _buffer.Rows) return false;
            int displayStart = Math.Max(0, _buffer.TotalLines - _buffer.Rows - _scrollOffset);
            return _buffer.LineSizeAtAbsoluteRow(displayStart + row - 1) != Ntilde.VT.LineSize.SingleWidth;
        }

        internal Point MouseReportCellCentre(int column, int row)
        {
            double cellWidth = _metrics.CellWidth;
            if (_buffer != null && row >= 1 && row <= _buffer.Rows)
            {
                int displayStart = Math.Max(0, _buffer.TotalLines - _buffer.Rows - _scrollOffset);
                if (_buffer.LineSizeAtAbsoluteRow(displayStart + row - 1) != Ntilde.VT.LineSize.SingleWidth) cellWidth *= 2;
            }
            return new Point((column - 0.5) * cellWidth, (row - 0.5) * _metrics.CellHeight);
        }

        /// <summary>
        /// The column under a horizontal position on the row at <paramref name="absRow"/>, clamped to the row. A
        /// double-size line (DECDWL / DECDHL) shows each of its cells two cells wide and holds half the columns.
        /// </summary>
        private int ColumnAtX(double x, int absRow)
        {
            if (_buffer == null || _metrics.CellWidth <= 0) return 0;
            Ntilde.VT.LineSize lineSize = _buffer.LineSizeAtAbsoluteRow(absRow);
            if (lineSize == Ntilde.VT.LineSize.SingleWidth)
                return Math.Clamp((int)(x / _metrics.CellWidth), 0, _buffer.Cols - 1);
            int columns = TerminalBuffer.ColumnsForLineSize(lineSize, _buffer.Cols);
            return Math.Clamp((int)(x / (_metrics.CellWidth * 2)), 0, columns - 1);
        }
    }
}
