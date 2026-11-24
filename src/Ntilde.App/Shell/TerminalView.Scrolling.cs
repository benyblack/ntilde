using Avalonia.Input;
using Avalonia.Threading;

namespace Ntilde.Shell
{
    /// <summary>
    /// The viewport's scroll offset: wheel and smooth scrolling, auto-scroll while selecting past an edge, and
    /// keeping the cursor in view as output arrives.
    /// </summary>
    public partial class TerminalView
    {
        private bool _enableSmoothScrolling = true;
        private int _targetScrollOffset;
        private long _scrollbackGeneration;
        private long _scrollbackTail;
        private int _scrollbackRows;
        // Carry fractional high-resolution wheel deltas so precision touchpads / hi-res
        // wheels (which emit sub-notch micro-events) produce one step per notch instead
        // of one per micro-event. One accumulator per wheel route, because each measures
        // notches at its own sensitivity; see EnterWheelRoute.
        private readonly WheelStepAccumulator _wheelScrollAccumulator = new();
        private readonly WheelStepAccumulator _wheelReportAccumulator = new();
        private readonly WheelStepAccumulator _wheelAlternateScrollAccumulator = new();
        private WheelRoute _wheelRoute;

        private enum WheelRoute { None, MouseReport, AlternateScroll, Scrollback }

        /// <summary>
        /// Drops every pending fraction when the wheel starts going somewhere else (mouse reporting
        /// toggled, alternate screen entered or left, ?1007 changed mid-gesture). A remainder is
        /// measured at its route's sensitivity, so carrying it across would let a quarter notch
        /// on the new route complete a step it never received; one left over from an earlier
        /// stint on the same route is just as stale.
        /// </summary>
        private void EnterWheelRoute(WheelRoute route)
        {
            if (route == _wheelRoute) return;
            _wheelRoute = route;
            _wheelScrollAccumulator.Reset();
            _wheelReportAccumulator.Reset();
            _wheelAlternateScrollAccumulator.Reset();
        }
        private double _wheelLinesPerNotch = 3.0;

        private void OnScrollAnimationTick(object? sender, EventArgs e)
        {
            AdvanceScrollAnimation();
        }

        /// <summary>
        /// One frame of the smooth-scroll easing toward the wheel target. Returns true while
        /// the animation is still in flight. Internal so tests can drive the animation to
        /// completion without a running dispatcher; the timer tick delegates here.
        /// </summary>
        internal bool AdvanceScrollAnimation()
        {
            int maxScroll = RebaseScrollOffsets();

            if (_scrollOffset == _targetScrollOffset)
            {
                _scrollAnimationTimer.Stop();
                return false;
            }

            int delta = _targetScrollOffset - _scrollOffset;
            int step = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 3);
            SetScrollOffsetCore(_scrollOffset + step, maxScroll);
            return IsWheelAnimationInFlight;
        }

        // Follow output near the live line, otherwise keep the historical anchor in view.
        public void EnsureCursorVisible()
        {
            if (_buffer == null) return;

            if (_justSwitchedFromAltScreen)
            {
                ScrollOffset = 0;
                _justSwitchedFromAltScreen = false;
                return;
            }

            // A smooth gesture owns the viewport even while passing through the
            // follow-output band. The live end is still represented by offset zero.
            if (IsWheelAnimationInFlight || _scrollOffset > 2)
            {
                RebaseScrollOffsets();
            }
            else if (_scrollOffset != 0)
            {
                ScrollOffset = 0;
            }
        }

        private (int Maximum, long Generation, long Tail, int Rows) ReadScrollbackPosition()
        {
            if (_buffer is not { } buffer) return default;
            buffer.Lock.EnterReadLock();
            try
            {
                if (buffer.IsAltScreenActive) return default;
                var history = buffer.Scrollback;
                return (history.Count, history.Generation, history.TotalRowsAppended, buffer.Rows);
            }
            finally { buffer.Lock.ExitReadLock(); }
        }

        private int RebaseScrollOffsets()
        {
            var (maximum, generation, tail, rows) = ReadScrollbackPosition();
            // The tail advances even when history evicts a page. Clamp only once
            // the anchored row itself has gone. Clear/reflow changes generation,
            // so the old absolute row coordinates no longer apply. A height
            // change can also move rows between the live grid and scrollback.
            long growth = generation == _scrollbackGeneration && rows == _scrollbackRows
                ? Math.Max(0, tail - _scrollbackTail)
                : 0;
            (_scrollbackGeneration, _scrollbackTail, _scrollbackRows) = (generation, tail, rows);
            _targetScrollOffset = OffsetAfterGrowth(_targetScrollOffset, growth, maximum);
            SetScrollOffsetCore(OffsetAfterGrowth(_scrollOffset, growth, maximum), maximum);
            return maximum;
        }

        private static int OffsetAfterGrowth(int offset, long growth, int maximum)
            => offset == 0 ? 0 : (int)Math.Min(offset + growth, maximum);

        public event Action<int, int>? ScrollStateChanged;
        private int _scrollOffset;
        private DispatcherTimer? _autoScrollTimer;
        private int _autoScrollDirection; // -1 up, 1 down

        /// <summary>
        /// The viewport's distance above the live line, in rows. Setting it is an external
        /// seek (typing, scrollbar, search, follow-output snap) and retires any smooth-scroll
        /// gesture still in flight.
        /// </summary>
        public int ScrollOffset
        {
            get => _scrollOffset;
            set => SeekScrollOffset(value);
        }

        /// <summary>True while a smooth wheel gesture still has frames to play.</summary>
        private bool IsWheelAnimationInFlight => _scrollOffset != _targetScrollOffset;

        private void SeekScrollOffset(int value)
        {
            if (_buffer == null) return;
            int maximum = MaxScrollOffset;
            if (value > 0)
            {
                (maximum, _scrollbackGeneration, _scrollbackTail, _scrollbackRows) = ReadScrollbackPosition();
            }
            _targetScrollOffset = Math.Clamp(value, 0, maximum);
            SetScrollOffsetCore(_targetScrollOffset, maximum);
        }

        private void SetScrollOffsetCore(int offset, int maximum)
        {
            if (_scrollOffset == offset) return;
            _scrollOffset = offset;
            ScrollStateChanged?.Invoke(offset, maximum);
            CommandAssistAnchorHintChanged?.Invoke();
            InvalidateBuffer();
        }

        /// <summary>Greatest reachable <see cref="ScrollOffset"/> for the attached buffer (0 when none).</summary>
        public int MaxScrollOffset => _buffer == null ? 0 : Math.Max(0, _buffer.TotalLines - _buffer.Rows);

        public void SetScrollOffset(int offset)
        {
            ScrollOffset = offset;
        }

        /// <summary>
        /// Returns the viewport to the live input line (offset 0). Typing while scrolled up
        /// into scrollback must show the line being written, so every user-originated input
        /// path snaps here first. No-op when already at the bottom or when there is nothing
        /// to scroll (e.g. the alternate screen, where maxScroll is 0).
        /// </summary>
        /// <remarks>
        /// Only <em>input</em> does this. Output keeps the user's position via
        /// <see cref="EnsureCursorVisible"/>'s near-bottom rule, and protocol-driven sends
        /// that are not writing (focus reports, mouse reports, device replies) must not
        /// scroll at all - those bypass <see cref="SendUserInput"/> by design.
        /// </remarks>
        public void ScrollToInputLine() => ScrollOffset = 0;

        private void StartAutoScroll(int direction)
        {
            _autoScrollDirection = direction;
            if (_autoScrollTimer == null)
            {
                _autoScrollTimer = new DispatcherTimer(DispatcherPriority.Render);
                _autoScrollTimer.Interval = TimeSpan.FromMilliseconds(50);
                _autoScrollTimer.Tick += OnAutoScrollTick;
            }
            if (!_retired && !_autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Start();
            }
        }

        private void StopAutoScroll()
        {
            if (_autoScrollTimer != null && _autoScrollTimer.IsEnabled)
            {
                _autoScrollTimer.Stop();
            }
        }

        private void OnAutoScrollTick(object? sender, EventArgs e)
        {
            if (_buffer == null || _autoScrollDirection == 0) return;

            // Adjust scroll offset
            int newOffset = ScrollOffset - _autoScrollDirection; // Offset decreases when scrolling down (towards 0/end)
                                                                 // Wait, ScrollOffset 0 is bottom (end). Higher values go back in history.
                                                                 // If dragging DOWN (direction=1), we want to see newer lines -> Decrease Offset.
                                                                 // If dragging UP (direction=-1), we want to see older lines -> Increase Offset.

            // Re-clamping logic:
            int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
            newOffset = Math.Clamp(newOffset, 0, maxScroll);

            if (newOffset != ScrollOffset)
            {
                ScrollOffset = newOffset;

                // Update selection to current mouse position relative to NEW scroll
                try
                {
                    // Accessing pointer position is tricky inside timer without event args.
                    // We can rely on the fact that OnPointerMoved updates _selection.End 
                    // BUT OnPointerMoved fires on mouse move. If mouse is still, we need to update selection end based on new scroll.
                    // Actually, simpler: The selection end is an absolute row. 
                    // If we scroll, the mouse is now over a DIFFERENT absolute row.

                    // We should track last known mouse position or just let the user move mouse.
                    // But standard behavior is: hold mouse at bottom -> scroll -> selection expands.
                    // To do this, we need to update _selection.End to the row currently at the bottom (or top) visual edge.

                    int targetVisualRow = (_autoScrollDirection > 0) ? _buffer.Rows - 1 : 0;

                    // Convert visual row to absolute row with NEW offset
                    int totalLines = _buffer.TotalLines;
                    int displayStart = Math.Max(0, totalLines - _buffer.Rows - ScrollOffset);
                    int absRow = displayStart + targetVisualRow;

                    // We need to keep the Column from the initial selection/drag. 
                    // But for full line selection feeling, usually it goes to end/start of line.
                    // Let's just update the Row, keep Col from existing selection end? No, that might be weird.
                    // Ideally we'd poll mouse position, but complex in Avalonia without reference.
                    // Let's assume extending to the full width of the new row is acceptable for vertical drag,
                    // or just keep the previous column.

                    _selection.End = (absRow, _selection.End.Col);
                    InvalidateVisual();
                }
                catch { }
            }
        }

        // Mouse event handlers
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "S3776", Justification = "A linear wheel router (mouse report, alternate scroll, scrollback); the generated-input guards from #999 are two necessary early-outs on that fixed order. Splitting it would hide the route precedence.")]
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            if (!CanApplyGeneratedInput) { e.Handled = true; return; }
            base.OnPointerWheelChanged(e);
            if (_buffer == null) return;

            // If mouse reporting is active, forward wheel notches to the session.
            // Accumulate fractional deltas so a high-resolution device emits one
            // WheelUp/WheelDown per notch rather than one per sub-notch micro-event
            // (which otherwise floods a TUI with scroll commands -> runaway scrolling).
            if (_buffer.IsMouseReportingActive())
            {
                EnterWheelRoute(WheelRoute.MouseReport);
                // Classic stepped wheel (full notch) forwards 1 event per notch so
                // list/menu TUIs advance one item; only sub-notch precision devices get
                // the smoothing multiplier.
                double reportUnitsPerNotch = WheelStepAccumulator.ReportUnitsPerNotch(e.Delta.Y, _wheelLinesPerNotch);
                int notches = _wheelReportAccumulator.Accumulate(e.Delta.Y, reportUnitsPerNotch);
                if (notches != 0)
                {
                    var point = e.GetCurrentPoint(this);
                    // Viewport-relative, 1-based - the wire coordinate space. Must agree with the
                    // press/release/motion paths so an app sees one consistent coordinate system.
                    var (reportColumn, reportRow) = ToMouseReportCell(point.Position);
                    var (reportPixelX, reportPixelY) = ToMouseReportPixel(point.Position);
                    var button = notches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
                    for (int i = 0; i < Math.Abs(notches); i++)
                    {
                        SendMouseEvent(new TerminalMouseEvent(
                            TerminalMouseEventKind.Wheel,
                            button,
                            reportColumn,
                            reportRow,
                            e.KeyModifiers,
                            reportPixelX,
                            reportPixelY));
                    }
                }
                e.Handled = true;
                return;
            }

            // A generated event never sends keys: the alternate-scroll route below is refused for it. Its standard branch is the view's own scrollback scroll, which sends nothing (#999 viewport control).
            if (_generatedInputInProgress && _buffer.IsAltScreenActive && _buffer.Modes.IsAlternateScroll) { e.Handled = true; return; }

            // Alternate scroll (?1007, issue #828): the alternate screen has no scrollback, so a
            // pager or TUI without mouse support (less, man, htop) gets one cursor key per notch
            // instead. Reporting above wins; the main screen keeps scrolling the scrollback.
            if (_buffer.IsAltScreenActive && _buffer.Modes.IsAlternateScroll)
            {
                EnterWheelRoute(WheelRoute.AlternateScroll);
                SendAlternateScroll(_wheelAlternateScrollAccumulator.Accumulate(e.Delta.Y, AlternateScrollKeysPerNotch));
                e.Handled = true;
                return;
            }

            // Standard scrolling. Accumulate fractional deltas (lines per notch) so
            // sub-notch micro-events are summed line-by-line instead of being truncated
            // to zero each event.
            // Scroll up (positive) -> Increase Offset; scroll down (negative) -> Decrease.
            EnterWheelRoute(WheelRoute.Scrollback);
            int delta = _wheelScrollAccumulator.Accumulate(e.Delta.Y, _wheelLinesPerNotch);
            if (delta != 0)
            {
                ScrollByWheelLines(delta);
            }
            if (_generatedInputInProgress) e.Handled = true;
        }

        /// <summary>
        /// One cursor key per notch, whatever <see cref="_wheelLinesPerNotch"/> says: that setting
        /// sizes Ntilde's own scrollback steps, not how far a program moves per key. Sub-notch
        /// deltas accumulate to whole notches.
        /// </summary>
        private const double AlternateScrollKeysPerNotch = 1.0;

        /// <summary>
        /// Sends <paramref name="notches"/> cursor keys (positive = up) encoded from this pane's own
        /// modes, so DECCKM picks <c>ESC O A</c> or <c>CSI A</c>. Modifiers are not carried: the
        /// wheel is not a key chord, and other terminals send the plain key here too.
        /// </summary>
        private void SendAlternateScroll(int notches)
        {
            if (notches == 0 || _buffer == null || _session == null) return;
            string key = TerminalInputModeEncoder.EncodeAlternateScrollKey(_buffer.Modes, notches > 0);
            SendUserInput(string.Concat(Enumerable.Repeat(key, Math.Abs(notches))));
            AlternateScrollKeysSent?.Invoke();
        }

        /// <summary>
        /// Cursor keys reached the session from the wheel (?1007), bypassing the key handling
        /// that observers such as the markless submission accumulator watch.
        /// </summary>
        public event Action? AlternateScrollKeysSent;

        /// <summary>
        /// Scroll the terminal's own scrollback by whole lines from a wheel gesture (positive =
        /// up into history). With smooth scrolling on this only moves the animation target;
        /// <see cref="AdvanceScrollAnimation"/> carries the viewport there over several frames.
        /// Internal seam for the wheel path, mirroring HandleKeyDownCore for keys.
        /// </summary>
        internal void ScrollByWheelLines(int lines)
        {
            if (_buffer == null || lines == 0) return;

            int maximum = RebaseScrollOffsets();
            int origin = _enableSmoothScrolling ? _targetScrollOffset : _scrollOffset;
            _targetScrollOffset = Math.Clamp(origin + lines, 0, maximum);
            if (_enableSmoothScrolling)
            {
                if (!_retired && !_scrollAnimationTimer.IsEnabled)
                {
                    _scrollAnimationTimer.Start();
                }
            }
            else
            {
                SetScrollOffsetCore(_targetScrollOffset, maximum);
            }
        }
    }
}
