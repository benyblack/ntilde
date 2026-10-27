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
        // Carry fractional high-resolution wheel deltas so precision touchpads / hi-res
        // wheels (which emit sub-notch micro-events) produce one step per notch instead
        // of one per micro-event. Separate accumulators for the two wheel code paths.
        private readonly WheelStepAccumulator _wheelScrollAccumulator = new();
        private readonly WheelStepAccumulator _wheelReportAccumulator = new();
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
            if (_buffer != null)
            {
                // The buffer may have shrunk since the gesture began (alt-screen switch, ED 3);
                // a target past the new ceiling would otherwise never be reached.
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
                _targetScrollOffset = Math.Clamp(_targetScrollOffset, 0, maxScroll);
            }

            if (_scrollOffset == _targetScrollOffset)
            {
                _scrollAnimationTimer.Stop();
                return false;
            }

            int delta = _targetScrollOffset - _scrollOffset;
            int step = Math.Sign(delta) * Math.Max(1, Math.Abs(delta) / 3);
            ApplyScrollOffset(_scrollOffset + step, retireWheelTarget: false);
            return IsWheelAnimationInFlight;
        }

        // Method to ensure the cursor is visible in the view
        public void EnsureCursorVisible()
        {
            if (_buffer == null) return;


            // For remote environments (WSL/SSH), after screen transitions, ensure we're at the bottom
            // where the prompt should be, rather than calculating based on cursor position
            // This addresses the issue where new output doesn't scroll properly after mc exits
            if (_justSwitchedFromAltScreen)
            {
                ScrollOffset = 0; // Always scroll to bottom after alt screen switch
                _justSwitchedFromAltScreen = false; // Reset the flag
            }
            else
            {
                // Calculate the ideal scroll offset to show the cursor at the bottom of the viewport
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);

                // A smooth wheel gesture still in flight owns the viewport: output landing
                // mid-gesture (a TUI redrawing its spinner) must not yank it back to the live
                // line while it is passing through the follow-output band below.
                if (IsWheelAnimationInFlight)
                {
                    _targetScrollOffset = Math.Clamp(_targetScrollOffset, 0, maxScroll);
                    return;
                }

                // Only follow the output if we're already near the bottom (within 2 lines)
                // This allows users to scroll up and stay there while still following new output when appropriate
                if (ScrollOffset <= 2)
                {
                    ScrollOffset = 0;
                }
                else
                {
                    // Maintain current scroll position if user has scrolled up
                    // Make sure it's still within valid range
                    ScrollOffset = Math.Min(ScrollOffset, maxScroll);
                }
            }
        }

        public event Action<int, int>? ScrollStateChanged;
        private int _scrollOffset = 0;
        private DispatcherTimer? _autoScrollTimer;
        private int _autoScrollDirection = 0; // -1 up, 1 down

        /// <summary>
        /// The viewport's distance above the live line, in rows. Setting it is an external
        /// seek (typing, scrollbar, search, follow-output snap) and retires any smooth-scroll
        /// gesture still in flight; the animation itself moves the offset through
        /// <see cref="ApplyScrollOffset"/> so its own frames do not cancel it.
        /// </summary>
        public int ScrollOffset
        {
            get => _scrollOffset;
            set => ApplyScrollOffset(value, retireWheelTarget: true);
        }

        /// <summary>True while a smooth wheel gesture still has frames to play.</summary>
        private bool IsWheelAnimationInFlight => _scrollOffset != _targetScrollOffset;

        private void ApplyScrollOffset(int value, bool retireWheelTarget)
        {
            if (_buffer == null) return;
            int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
            int newValue = Math.Clamp(value, 0, maxScroll);
            if (retireWheelTarget)
            {
                _targetScrollOffset = newValue;
            }
            if (_scrollOffset != newValue)
            {
                _scrollOffset = newValue;
                ScrollStateChanged?.Invoke(_scrollOffset, maxScroll);
                CommandAssistAnchorHintChanged?.Invoke();
                InvalidateBuffer();
            }
        }

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
        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            base.OnPointerWheelChanged(e);
            if (_buffer == null) return;

            // If mouse reporting is active, forward wheel notches to the session.
            // Accumulate fractional deltas so a high-resolution device emits one
            // WheelUp/WheelDown per notch rather than one per sub-notch micro-event
            // (which otherwise floods a TUI with scroll commands -> runaway scrolling).
            if (_buffer.IsMouseReportingActive())
            {
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
                    var button = notches > 0 ? TerminalMouseButton.WheelUp : TerminalMouseButton.WheelDown;
                    for (int i = 0; i < Math.Abs(notches); i++)
                    {
                        SendMouseEvent(new TerminalMouseEvent(
                            TerminalMouseEventKind.Wheel,
                            button,
                            reportColumn,
                            reportRow,
                            e.KeyModifiers));
                    }
                }
                e.Handled = true;
                return;
            }

            // Standard scrolling. Accumulate fractional deltas (lines per notch) so
            // sub-notch micro-events are summed line-by-line instead of being truncated
            // to zero each event.
            // Scroll up (positive) -> Increase Offset; scroll down (negative) -> Decrease.
            int delta = _wheelScrollAccumulator.Accumulate(e.Delta.Y, _wheelLinesPerNotch);
            if (delta != 0)
            {
                ScrollByWheelLines(delta);
            }
        }

        /// <summary>
        /// Scroll the terminal's own scrollback by whole lines from a wheel gesture (positive =
        /// up into history). With smooth scrolling on this only moves the animation target;
        /// <see cref="AdvanceScrollAnimation"/> carries the viewport there over several frames.
        /// Internal seam for the wheel path, mirroring HandleKeyDownCore for keys.
        /// </summary>
        internal void ScrollByWheelLines(int lines)
        {
            if (_buffer == null || lines == 0) return;

            if (_enableSmoothScrolling)
            {
                int maxScroll = Math.Max(0, _buffer.TotalLines - _buffer.Rows);
                _targetScrollOffset = Math.Clamp(_targetScrollOffset + lines, 0, maxScroll);
                if (!_retired && !_scrollAnimationTimer.IsEnabled)
                {
                    _scrollAnimationTimer.Start();
                }
            }
            else
            {
                ScrollOffset += lines;
            }
        }
    }
}
