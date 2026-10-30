using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Ntilde.VT;
using Ntilde.Rendering;

namespace Ntilde.Shell
{
    /// <summary>
    /// Fitting the grid to the view: computing it from the size, throttling the resize sent to the session, and
    /// the deferred mode in which the session resizes the buffer itself.
    /// </summary>
    public partial class TerminalView
    {
        public event Action<int, int>? Ready;
        public event Action<int, int>? OnResize;
        private bool _isReady;

        // Discrete resize: the grid the buffer and the PTY were last actually told about, so a
        // redundant dispatch can be skipped. Written where the dispatch happens and nowhere else
        // (#432): recording a grid at the moment it was *computed* meant a dispatch cancelled
        // before the throttle ran still counted as sent, and the buffer was then described as
        // being on a grid it had never been given.
        private int _lastDispatchedCols;
        private int _lastDispatchedRows;

        // Deferred mode (Phase 2 spec §8): the grid last *requested* via OnResize. The buffer is resized
        // by the session (in stream order), so "did the grid change" compares against the request, and
        // _lastDispatched* records only what the buffer actually became (NotifySessionResizedBuffer).
        private int _lastRequestedCols;
        private int _lastRequestedRows;

        /// <summary>
        /// Set by the pane for a session that orders resizes in its output stream: OnResize is then only a
        /// request, and the buffer changes size when the session says so. Everything else in the view
        /// reads the buffer's size under its lock at use time.
        /// </summary>
        internal bool DefersBufferResizeToSession { get; set; }

        internal (int Cols, int Rows) LastRequestedGridForTest => (_lastRequestedCols, _lastRequestedRows);

        private bool GridDiffersFromLastSent(int cols, int rows) => DefersBufferResizeToSession
            ? cols != _lastRequestedCols || rows != _lastRequestedRows
            : cols != _lastDispatchedCols || rows != _lastDispatchedRows;

        /// <summary>After raising OnResize for (cols, rows): record the request, and the dispatch only if the buffer really is that size.</summary>
        private void RecordGridSent(int cols, int rows)
        {
            _lastRequestedCols = cols;
            _lastRequestedRows = rows;
            if (!DefersBufferResizeToSession || (_buffer is { } b && b.Cols == cols && b.Rows == rows)) RecordDispatchedGrid(cols, rows);
        }

        /// <summary>UI thread: the session resized the buffer (snapshot restore or an in-stream resize).</summary>
        internal void NotifySessionResizedBuffer()
        {
            Dispatcher.UIThread.VerifyAccess();
            if (_buffer is not { } buffer) return;
            RecordDispatchedGrid(buffer.Cols, buffer.Rows);
            ResetMouseMotionTracking();
            _renderCaches.Rows.MaxEntries = Math.Max(buffer.Rows * 3, 50);
            _renderCaches.Rows.RequestClear();
            ApplyScrollOffset(_scrollOffset, retireWheelTarget: true); // re-clamp against the new TotalLines/Rows
            InvalidateBuffer();
        }

        // Throttle resize: limit how often we send resize to PTY (interval-based, not debounce)
        private DateTime _lastPtyResizeTime = DateTime.MinValue;
        private DispatcherTimer? _resizeThrottleTimer;
        private int _pendingCols;
        private int _pendingRows;
        private DateTime _pendingResizeStartedAt = DateTime.MinValue;

        /// <summary>
        /// Overrides the throttle interval check for a test: <c>false</c> forces the next dispatch
        /// onto the timer, null defers to the clock.
        /// </summary>
        /// <remarks>
        /// A test about a deferred dispatch has to be certain the dispatch really was deferred,
        /// and that is decided by a wall-clock comparison. A seam that only nudged the timestamp
        /// would still be racing it - a worker that stalled for 60ms between arranging the setup
        /// and pumping the dispatcher would take the other branch and fail on its own scheduling.
        /// Overriding the decision rather than the clock removes the race instead of narrowing it.
        /// Both branches remain real code; this only chooses which one runs.
        /// </remarks>
        // Set when StopUiTimers cancels a dispatch that was armed and waiting. Reconciliation
        // is gated on it so that restoring a pane re-sends only a grid that was genuinely
        // dropped, and never becomes a second, unasked-for resize path (#432).
        private bool _resizeDispatchCancelled;

        private bool? _throttleElapsedForTest;

        /// <summary>Forces the next dispatch onto the throttle timer rather than the inline path.</summary>
        internal void HoldResizeThrottleForTest() => _throttleElapsedForTest = false;

        /// <summary>Forces the next dispatch inline rather than onto the timer.</summary>
        /// <remarks>
        /// Needed because the headless harness never ticks a DispatcherTimer, so a test that
        /// waited on one would sleep or hang.
        /// </remarks>
        internal void ReleaseResizeThrottleForTest() => _throttleElapsedForTest = true;

        /// <summary>Whether a resize has been computed and is still waiting on the throttle.</summary>
        internal bool ResizeDispatchPendingForTest => _resizeThrottleTimer?.IsEnabled == true;

        /// <summary>The grid the buffer and the PTY were last actually told about.</summary>
        internal (int Cols, int Rows) LastDispatchedGridForTest => (_lastDispatchedCols, _lastDispatchedRows);

        /// <summary>The grid a queued dispatch will send when it runs.</summary>
        internal (int Cols, int Rows) PendingGridForTest => (_pendingCols, _pendingRows);

        /// <summary>
        /// The cell grid a control of <paramref name="size"/> can show, or <see langword="false"/>
        /// when it cannot show one at all and the caller must keep the size it already had.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This used to clamp instead of refuse - <c>Math.Max(cols, 1)</c>, <c>Math.Max(rows, 1)</c>
        /// - so a control with no size at all reported a 1x1 grid, and that is a destructive thing
        /// to tell a terminal buffer. Resizing to a single column re-wraps the whole transcript one
        /// character per row, which blows the scrollback budget and evicts nearly all of it, and the
        /// eviction is permanent: growing back cannot restore what the budget already discarded.
        /// Measured on the buffer directly, a plain resize against a 1x1 round trip:
        /// </para>
        /// <code>
        /// 2000 lines: plain resize keeps 2000, a 1x1 round trip keeps 80
        ///  200 lines: plain resize keeps  200, a 1x1 round trip keeps 72
        /// </code>
        /// <para>
        /// It also reaches the shell. A 1x1 grid is sent on to the PTY, so the child reflows its
        /// prompt for a one-column terminal and redraws for it.
        /// </para>
        /// <para>
        /// <c>TerminalBuffer.Resize</c> already refuses non-positive dimensions for this reason,
        /// keeping the last valid size until a real one arrives. The clamp here defeated that guard
        /// by manufacturing a positive number from a zero, so the buffer never saw the degenerate
        /// value it knows to ignore. Refusing keeps the two agreeing.
        /// </para>
        /// <para>
        /// Zero size is ordinary, not exotic: a control that has not been laid out yet, one in a
        /// collapsed or hidden container, and one detached from the visual tree all measure zero.
        /// </para>
        /// </remarks>
        internal static bool TryComputeGrid(Size size, double cellWidth, double cellHeight, out int cols, out int rows)
        {
            cols = 0;
            rows = 0;

            if (cellWidth <= 0 || cellHeight <= 0)
            {
                return false;
            }

            // Non-finite extents are refused by name. Zero and negative ones need no check of their
            // own - they divide to a non-positive cell count and fall out of the comparison below -
            // and NaN converts to 0, so it does too. Infinity does NOT: (int)(double.Infinity / w)
            // is int.MaxValue, which sails through a "> 0" test as a two-billion-column grid. An
            // earlier draft of this guard asserted in a comment that both were handled by the
            // arithmetic; only one of them is.
            if (!double.IsFinite(size.Width) || !double.IsFinite(size.Height))
            {
                return false;
            }

            // Padding must match TerminalDrawOperation (PaddingLeft = 4); subtracting it here
            // avoids clipping the last column.
            double availableWidth = size.Width - 4;

            int candidateCols = (int)(availableWidth / cellWidth);
            int candidateRows = (int)(size.Height / cellHeight);

            // A fractional cell is not a cell. Refusing rather than rounding up to one keeps a
            // sliver of a pane from being described as a one-column terminal.
            //
            // Both out parameters stay zero unless BOTH dimensions are usable, so a refusal never
            // hands back a half-computed grid that reads as a real one - a caller that ignored the
            // return value would otherwise get a plausible column count beside a zero row count.
            if (candidateCols <= 0 || candidateRows <= 0)
            {
                return false;
            }

            cols = candidateCols;
            rows = candidateRows;
            return true;
        }

        protected override void OnSizeChanged(SizeChangedEventArgs e)
        {
            try
            {
                base.OnSizeChanged(e);

                // Row pictures encode snapped positions for a specific geometry.
                // Clear immediately on size changes so startup/layout transitions
                // cannot reuse stale pictures until the throttled resize path runs.
                _renderCaches.Rows.RequestClear();

                // Force immediate render pass on any size change to prevent white panes
                InvalidateVisual();

                if (_buffer != null)
                {
                    if (_metrics.CellWidth <= 0 || _metrics.CellHeight <= 0)
                    {
                        MeasureCharSize();
                    }

                    if (_metrics.CellWidth <= 0 || _metrics.CellHeight <= 0) return; // Still zero? Bail.

                    if (!TryComputeGrid(e.NewSize, _metrics.CellWidth, _metrics.CellHeight, out int cols, out int rows))
                    {
                        // Not laid out yet, or too small to hold a single cell. Keep the last
                        // good size; a real layout pass will follow. See TryComputeGrid for why
                        // this must not fall back to a 1x1 grid.
                        return;
                    }

                    // The grid the view is now drawing. Recorded unconditionally, even when no
                    // dispatch is needed, so that a dispatch already waiting on the throttle sends
                    // the size the view actually ended up at rather than the one that armed it -
                    // a resize that goes 80 -> 70 -> 80 inside one 60ms window would otherwise
                    // leave 70 queued behind a view that is back at 80.
                    _pendingCols = cols;
                    _pendingRows = rows;

                    // DISCRETE RESIZE: Only trigger actual resize when cell dimensions change
                    bool dimensionsChanged = GridDiffersFromLastSent(cols, rows);

                    if (!_isReady)
                    {
                        _isReady = true;
                        // No session is attached yet at first layout, so DefersBufferResizeToSession
                        // is still false here regardless of what the pane will set once it is ready
                        // (see Ready below); the initial grid always applies straight to the buffer.
                        if (_buffer != null) _buffer.Resize(cols, rows);
                        ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
                        Ready?.Invoke(cols, rows);

                        // Also trigger initial PTY resize to sync with layout
                        OnResize?.Invoke(cols, rows);

                        // This path dispatches inline rather than through the throttle, so it
                        // records the dispatch itself.
                        RecordGridSent(cols, rows);
                    }

                    if (dimensionsChanged)
                    {
                        QueueResizeDispatch();
                    }
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Error("OnSizeChanged failed. " + ex);
            }
        }


        /// <summary>
        /// Records the grid just handed to the buffer and the PTY.
        /// </summary>
        /// <remarks>
        /// One method rather than two assignments at each of the three dispatch sites, because
        /// the sites are easy to miss: the font branch of ApplySettings was overlooked in the
        /// first draft of this change, and an unrecorded dispatch there would have left the
        /// field describing a grid that had since been superseded. A fourth site added later
        /// gets the whole of the bookkeeping or none of it, not half.
        ///
        /// Clearing the cancellation flag belongs here for the same reason. A dispatch that has
        /// happened supersedes one that was dropped, so there is nothing left to repair - and a
        /// stale flag is not harmless, because the next restore would then reconcile against a
        /// grid the font path had legitimately chosen and resize away from it.
        /// </remarks>
        private void RecordDispatchedGrid(int cols, int rows)
        {
            _lastDispatchedCols = cols;
            _lastDispatchedRows = rows;
            _resizeDispatchCancelled = false;
        }

        /// <summary>
        /// Sends the pending grid to the buffer and the PTY, now if the throttle interval has
        /// elapsed and on the timer otherwise.
        /// </summary>
        private void QueueResizeDispatch()
        {
            // STRICT INTERVAL THROTTLE: Limit resize dispatch to 60ms
            var now = DateTime.UtcNow;
            if (_pendingResizeStartedAt == DateTime.MinValue)
            {
                _pendingResizeStartedAt = now;
            }

            if (_resizeThrottleTimer == null)
            {
                _resizeThrottleTimer = new DispatcherTimer(DispatcherPriority.Normal)
                {
                    Interval = TimeSpan.FromMilliseconds(60)
                };
                _resizeThrottleTimer.Tick += OnResizeThrottleTick;
            }

            bool intervalElapsed =
                _throttleElapsedForTest ?? ((now - _lastPtyResizeTime).TotalMilliseconds >= 60);

            if (intervalElapsed && !_resizeThrottleTimer.IsEnabled)
            {
                // Enough time passed and no pending timer - send immediately
                SendThrottledResize();
            }
            else if (!_retired && !_resizeThrottleTimer.IsEnabled)
            {
                _resizeThrottleTimer.Start();
            }
        }

        /// <summary>
        /// Re-dispatches the current grid if the buffer and the PTY are not on it. Called when the
        /// view becomes renderable again.
        /// </summary>
        /// <remarks>
        /// StopUiTimers cancels a dispatch that was waiting on the throttle - collapsing a pane,
        /// hiding it, or detaching it all reach it - and StartUiTimers deliberately does not
        /// restart that timer. Honest bookkeeping alone does not recover from that: OnSizeChanged
        /// fires on a size *change*, and a pane that is collapsed and restored comes back at the
        /// size it left at, so no further event ever arrives to notice the disagreement. Measured,
        /// not assumed - the bookkeeping half of #432 was tested on its own first and the pane was
        /// still stranded on its old grid.
        ///
        /// Nothing is sent when the grids already agree, so the ordinary show/hide of a pane whose
        /// size never changed costs a comparison.
        /// </remarks>
        private void ReconcileDispatchedGrid()
        {
            // Only ever a repair for a dispatch that was actually cancelled. Reconciling on
            // every restore would make this a second resize path with a different opinion:
            // the font branch of ApplySettings derives its grid as Bounds.Width / CellWidth
            // while TryComputeGrid subtracts the padding the draw operation reserves, so the
            // two disagree by a column at some widths - and an ungated reconcile would turn
            // that standing disagreement into a reflow and a SIGWINCH every time someone
            // collapsed and reopened a pane. Repairing a cancellation is this method's job;
            // arbitrating between two grid calculations is not.
            if (!_resizeDispatchCancelled) return;
            if (!_isUiRenderable) return;
            if (_buffer == null || !_isReady) return;
            if (_metrics.CellWidth <= 0 || _metrics.CellHeight <= 0) return;

            if (!TryComputeGrid(Bounds.Size, _metrics.CellWidth, _metrics.CellHeight, out int cols, out int rows))
            {
                // Nothing usable to reconcile against; keep the flag and let a real layout
                // pass, or the next restore, try again.
                return;
            }

            _resizeDispatchCancelled = false;

            // Deferred mode compares against the last *requested* grid (GridDiffersFromLastSent),
            // not the buffer's - the session may not have caught up to an earlier request yet.
            // Routing through QueueResizeDispatch -> SendThrottledResize is what actually gives
            // deferred mode its "raise OnResize and record the request, without touching the
            // buffer" behaviour; both already branch on DefersBufferResizeToSession there.
            if (!GridDiffersFromLastSent(cols, rows)) return;

            _pendingCols = cols;
            _pendingRows = rows;
            QueueResizeDispatch();
        }

        private void SendThrottledResize()
        {
            try
            {
                if (_pendingCols > 0 && _pendingRows > 0 && _buffer != null)
                {
                    _lastPtyResizeTime = DateTime.UtcNow;

                    // CRITICAL ORDER: Resize buffer FIRST (synchronously, under lock)
                    // THEN notify PTY (triggers SIGWINCH, new output uses new size)
                    // This prevents race where PTY sends data for new dimensions while buffer is mid-reflow
                    //
                    // Deferred mode (Phase 2 spec §8): a session that orders resizes in its stream
                    // owns the buffer's size, so this becomes only a request - the buffer is resized
                    // later, on the delivery thread, by NotifySessionResizedBuffer.
                    if (!DefersBufferResizeToSession)
                    {
                        _buffer.Resize(_pendingCols, _pendingRows);
                        _renderCaches.Rows.MaxEntries = Math.Max(_pendingRows * 3, 50);
                        _renderCaches.Rows.RequestClear();
                    }

                    // Recorded here, where the dispatch actually happens, and not where the grid
                    // was computed - see the field declarations and #432.
                    RecordGridSent(_pendingCols, _pendingRows);

                    ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
                    OnResize?.Invoke(_pendingCols, _pendingRows);
                    if (_pendingResizeStartedAt != DateTime.MinValue)
                    {
                        long latencyMs = (long)Math.Max(0, (DateTime.UtcNow - _pendingResizeStartedAt).TotalMilliseconds);
                        RendererStatistics.RecordResizeDispatchLatency(latencyMs);
                        _pendingResizeStartedAt = DateTime.MinValue;
                    }

                    InvalidateBuffer();
                }
            }
            catch (Exception ex)
            {
                TerminalLogger.Error("SendThrottledResize failed. " + ex);
            }
        }

        private void OnResizeThrottleTick(object? sender, EventArgs e)
        {
            // Timer fired - send any pending resize
            _resizeThrottleTimer?.Stop();
            SendThrottledResize();
        }
    }
}
