using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Ntilde.VT;
using Ntilde.Rendering;
using Ntilde.Pty;

namespace Ntilde.Shell
{
    public struct CellMetrics
    {
        public float CellWidth { get; set; }
        public float CellHeight { get; set; }
        public float Baseline { get; set; }
        public float Ascent { get; set; }
        public float Descent { get; set; }
        public float Leading { get; set; }
    }

    public readonly record struct CommandAssistPromptHint(
        int VisibleCursorVisualRow,
        int VisibleRows,
        float CellWidth,
        float CellHeight);

    /// <summary>
    /// Where an <c>OSC 133;B</c> mark currently sits on screen. The trusted counterpart to
    /// <see cref="CommandAssistPromptHint"/>: that one reports where the cursor is and lets the
    /// consumer guess the prompt row, this one reports the prompt row itself.
    /// </summary>
    /// <param name="VisibleMarkVisualRow">
    /// Zero-based row inside the viewport holding the first cell of the user's input.
    /// </param>
    /// <remarks>
    /// Deliberately not shape-parity with <see cref="CommandAssistPromptHint"/>: that record also
    /// carries a <c>CellWidth</c> nothing reads, and copying an unread field for symmetry would
    /// only make a second place to keep in sync. Vertical anchoring needs a row, a row count, and
    /// a row height; when a consumer needs a column, add it then.
    /// </remarks>
    public readonly record struct CommandAssistMarkAnchorHint(
        int VisibleMarkVisualRow,
        int VisibleRows,
        float CellHeight);

    public partial class TerminalView : Control
    {
        public CellMetrics Metrics => _metrics;

        /// <summary>
        /// Fired when font metrics (cell width/height) change.
        /// </summary>
        public event Action<float, float>? MetricsChanged;
        public event Action? CommandAssistAnchorHintChanged;

        private bool _showRenderHud;
        public bool ShowRenderHud
        {
            get => _showRenderHud;
            set
            {
                if (_showRenderHud != value)
                {
                    _showRenderHud = value;
                    _isDirty = true;
                    if (_isUiRenderable) InvalidateVisual();
                }
            }
        }

        public TerminalView()
        {
            Focusable = true;
            ClipToBounds = true;

            // Allow receiving focus via click
            PointerPressed += (s, e) => Focus();

            // Created stopped (#543). The DispatcherTimer constructor that takes a callback also starts the timer, and a
            // running timer is rooted by the dispatcher and holds this view, its pane and its window: a view that never
            // reached the visual tree ticked all four forever, and kept its window alive after the window closed.
            // StartUiTimers and OnAttachedToVisualTree start them once the view can be drawn.
            _renderTimer = CreateStoppedTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnRenderTimerTick);

            _cursorBlinkTimer = CreateStoppedTimer(TimeSpan.FromMilliseconds(530), DispatcherPriority.Render, OnCursorBlinkTick);
            _scrollAnimationTimer = CreateStoppedTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, OnScrollAnimationTick);
            _metricsTimer = CreateStoppedTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background, OnMetricsTimerTick);

            DragDrop.SetAllowDrop(this, true);
            AddHandler(DragDrop.DragOverEvent, OnDragOver);
            AddHandler(DragDrop.DropEvent, async (s, e) =>
            {
                try
                {
                    await OnDropAsync(s, e);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[TerminalView] OnDropAsync Failed: {ex}");
                }
            });
        }

        private static DispatcherTimer CreateStoppedTimer(TimeSpan interval, DispatcherPriority priority, EventHandler tick)
        {
            var timer = new DispatcherTimer(priority) { Interval = interval };
            timer.Tick += tick;
            return timer;
        }

        private TerminalBuffer? _buffer;
        public TerminalBuffer? Buffer => _buffer;

        // Coalescing
        private bool _isDirty;
        private DispatcherTimer _renderTimer;
        private readonly DispatcherTimer _cursorBlinkTimer;
        private readonly DispatcherTimer _scrollAnimationTimer;
        private bool _uiTimersRunning;
        private bool _isAttachedToVisualTree;
        private volatile bool _isUiRenderable;
        private bool _cursorBlinkPhase = true;
        private bool _cursorBlinkEnabled = true;
        private bool _bellAudioEnabled = true;
        private bool _bellVisualEnabled = true;
        private bool _isBellFlashActive;
        private CursorStyle _preferredCursorStyle = CursorStyle.Underline;
        private int _lastCursorRow = -1;
        private int _lastCursorCol = -1;
        private long _lastHudUpdateTicks;
        private readonly DispatcherTimer _metricsTimer;

        public void TriggerBell()
        {
            if (_bellAudioEnabled)
            {
                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        Console.Beep(880, 35);
                    }
                    else
                    {
                        Console.Beep();
                    }
                }
                catch
                {
                    // Audio bell is best-effort.
                }
            }

            if (_bellVisualEnabled)
            {
                _isBellFlashActive = true;
                _isDirty = true;
                DispatcherTimer.RunOnce(() =>
                {
                    _isBellFlashActive = false;
                    _isDirty = true;
                }, TimeSpan.FromMilliseconds(90));
            }
        }
        private CellMetrics _metrics;
        private double _windowOpacity = 1.0;
        private bool _enableLigatures;
        private bool _enableComplexShaping = true;
        private double _minimumContrast = 1.0;
        private TerminalTheme? _appliedTheme; // a copy of the theme ApplySettings last gave the buffer
        // The glyph atlas and the row pictures, which this view and its draw operations share (#644).
        private readonly TerminalRenderCaches _renderCaches = new(new GlyphCache(), new RowImageCache());
        internal TerminalRenderCaches RenderCachesForTest => _renderCaches;
        private double _lastRenderScalingForRowCache = -1.0;
        private TopLevel? _cachedTopLevel;
        private double _cachedRenderScaling = 1.0;

        /// <summary>Ligature setting currently in effect (for snapshot capture).</summary>
        internal bool EnableLigatures => _enableLigatures;
        internal double MinimumContrast => _minimumContrast;
        internal CursorStyle PreferredCursorStyle => _preferredCursorStyle;

        /// <summary>Complex-shaping setting currently in effect (for snapshot capture).</summary>
        internal bool EnableComplexShaping => _enableComplexShaping;

        public int Cols => (_metrics.CellWidth > 0) ? (int)(Math.Max(0, Bounds.Width - 4) / _metrics.CellWidth) : 0;
        public int Rows => (_metrics.CellHeight > 0) ? (int)(Bounds.Height / _metrics.CellHeight) : 0;

        internal CommandAssistPromptHint? GetCommandAssistPromptHint()
        {
            if (_buffer == null || _metrics.CellHeight <= 0 || Rows <= 0)
            {
                return null;
            }

            int visualCursorRow;
            int visibleRows = Rows;
            _buffer.Lock.EnterReadLock();
            try
            {
                visualCursorRow = _buffer.GetVisualCursorRow(_scrollOffset);
            }
            finally
            {
                _buffer.Lock.ExitReadLock();
            }

            if (visibleRows <= 0 || visualCursorRow < 0 || visualCursorRow >= visibleRows)
            {
                return null;
            }

            return new CommandAssistPromptHint(
                VisibleCursorVisualRow: visualCursorRow,
                VisibleRows: visibleRows,
                CellWidth: _metrics.CellWidth,
                CellHeight: _metrics.CellHeight);
        }

        /// <summary>
        /// Resolves <paramref name="mark"/> against the buffer and the current scroll position.
        /// </summary>
        /// <remarks>
        /// Returns <c>null</c> whenever the marked row is not on screen — scrolled away, aged out
        /// of history, from a dead coordinate generation, or on the alt screen — and whenever there
        /// is no viewport to be on screen in (no buffer, no metrics, zero rows). "In the viewport"
        /// is the whole contract: a caller that gets a row back may place against it unconditionally.
        /// Because the answer depends on <see cref="ScrollOffset"/>, it must be re-asked on every
        /// placement pass; <see cref="CommandAssistAnchorHintChanged"/> fires on scroll for exactly
        /// that reason.
        /// </remarks>
        internal CommandAssistMarkAnchorHint? GetCommandAssistMarkAnchorHint(ShellIntegrationMark mark)
        {
            if (_buffer == null || _metrics.CellHeight <= 0 || Rows <= 0)
            {
                return null;
            }

            int visibleRows = Rows;
            if (!ShellMarkAnchorResolver.TryResolveVisualRow(_buffer, mark, _scrollOffset, visibleRows, out int visualRow))
            {
                return null;
            }

            return new CommandAssistMarkAnchorHint(
                VisibleMarkVisualRow: visualRow,
                VisibleRows: visibleRows,
                CellHeight: _metrics.CellHeight);
        }

        internal void SetMetricsForTest(float cellWidth, float cellHeight)
        {
            _metrics.CellWidth = cellWidth;
            _metrics.CellHeight = cellHeight;
        }

        /// <summary>The measured cell size, so a test can recompute the grid the view is drawing.</summary>
        internal (double Width, double Height) CellSizeForTest => (_metrics.CellWidth, _metrics.CellHeight);

        // Session for sending mouse events
        private ITerminalSession? _session;

        public void SetBuffer(TerminalBuffer buffer)
        {
            ResetKittyKeyTracking();
            if (_buffer != null)
            {
                _buffer.OnInvalidate -= InvalidateBuffer; // Idempotent remove
                _buffer.OnScreenSwitched -= OnScreenSwitched;
            }
            _buffer = buffer;
            if (_buffer != null)
            {
                _buffer.OnInvalidate -= InvalidateBuffer; // Ensure no duplicates
                _buffer.OnInvalidate += InvalidateBuffer;
                _buffer.OnScreenSwitched += OnScreenSwitched;
            }

            // Issue #269: a newly attached buffer has no notion of "the cell we last
            // reported motion for" - avoid suppressing its first hover/drag report.
            ResetMouseMotionTracking();

            MeasureCharSize();
            RefreshCursorBlinkTimerState();
            InvalidateVisual();
        }

        private bool _justSwitchedFromAltScreen;

        private void OnScreenSwitched(bool isAltScreen)
        {
            // CRITICAL: Clear the row picture cache on every screen switch.
            // AltScreen rows are reused objects whose revision counters can cycle back to
            // previously-cached values, causing stale blank SKPictures to be served.
            _renderCaches.Rows.RequestClear();

            // Issue #269: ?47/?1047/?1049 replaces every cell under a stationary pointer, so the
            // incoming application's first hover must report. Without this, a fresh session with
            // empty scrollback (coordinates identical across the switch) has that first hover
            // coalesced away as a duplicate and the app draws no highlight until the pointer
            // crosses a full cell boundary.
            ResetMouseMotionTracking();

            // When switching back from alt screen to main screen, mark that transition
            // to ensure the next content update resets scroll position
            if (!isAltScreen && _buffer != null)
            {
                _justSwitchedFromAltScreen = true;
                // Schedule scroll to cursor position after switching back to main screen
                // Add a slight delay to handle potential buffering in remote connections
                Dispatcher.UIThread.Post(async () =>
                {
                    // Small delay to allow any buffered output to be processed
                    await Task.Delay(10);
                    // Scroll to show the current cursor position
                    EnsureCursorVisible();
                }, DispatcherPriority.Render);
            }
            else
            {
                _justSwitchedFromAltScreen = false;
            }
        }

        // Property to allow external components to check if we just switched from alt screen
        public bool JustSwitchedFromAltScreen
        {
            get => _justSwitchedFromAltScreen;
            set => _justSwitchedFromAltScreen = value;
        }

        /// <summary>
        /// The session keystrokes, text, mouse reports and drops are sent to. Null stops all of it:
        /// keys then go unhandled, so Enter bubbles to the pane's reconnect handler (a pane whose
        /// multiplexer session is no longer attached uses this).
        /// </summary>
        public void SetSession(ITerminalSession? session)
        {
            if (!ReferenceEquals(_session, session)) ResetKittyKeyTracking();
            _session = session;
        }

        internal ITerminalSession? SessionForTest => _session;

        public void InvalidateBuffer()
        {
            // Any thread (the parse thread, mostly): the hover keys its cached link runs on this.
            Interlocked.Increment(ref _contentGeneration);
            _isDirty = true;
            if (!_isUiRenderable)
            {
                RendererStatistics.RecordHiddenInvalidationRequest();
            }
            // We rely on _renderTimer (16ms) to check _isDirty and call InvalidateVisual.
            // This acts as a swap-chain throttle, preventing the PTY from flooding the UI thread 
            // with millions of InvalidateVisual calls during cat/heavy output.
        }
    }
}
