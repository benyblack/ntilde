using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using SkiaSharp;
using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// Drawing a frame, rasterizing the view offscreen for a screenshot, and disposing retired inline images at the
    /// frame boundary.
    /// </summary>
    public partial class TerminalView
    {
        // Retired inline-image handles: this view OWNS their disposal. The drain lives here —
        // not in TerminalDrawOperation — because that op is also driven by
        // TerminalSnapshotRenderer (agent-host capture) on its own thread, so a draw-op drain
        // could dispose a bitmap another concurrent snapshot is still drawing. Safety is
        // exact, not a duration guess: every render pass brackets itself in a buffer
        // snapshot session, and the buffer only releases a retire entry once no session
        // started at or before its retire tick is still active — an in-flight capture of any
        // duration blocks disposal. The grace below is merely the fast-path bound; the
        // session gate is the correctness guarantee.
        private const long RetiredImageDisposalGraceMs = 2000;
        private readonly List<object> _retiredImageHandleScratch = new();

        /// <summary>
        /// Drains eligible retired bitmap or deferred-source handles. Called at frame boundaries
        /// and after hidden mux output; tests may supply an explicit age cutoff.
        /// </summary>
        internal void DisposeRetiredImageBitmaps() =>
            DisposeRetiredImageBitmaps(Environment.TickCount64 - RetiredImageDisposalGraceMs);

        /// <summary>
        /// Disposes snapshot-eligible handles outside VT locks; failed native disposal is requeued without returning credit.
        /// </summary>
        internal void DisposeRetiredImageBitmaps(long drainTick)
        {
            var buffer = _buffer;
            if (buffer == null) return;

            if (!buffer.DrainRetiredImageHandles(_retiredImageHandleScratch, drainTick))
            {
                return;
            }

            foreach (var handle in _retiredImageHandleScratch)
            {
                if (handle is IDisposable bitmap)
                {
                    try
                    {
                        bitmap.Dispose();
                    }
                    catch
                    {
                        // Native pixel credit remains charged. Retain the failed owner in
                        // its original buffer queue for a later frame, without readmission.
                        // Legacy failures keep their existing best-effort behavior.
                        buffer.RetryRetiredImageDisposal(handle);
                    }
                }
            }
            _retiredImageHandleScratch.Clear();
        }

        // Once a view switches streams, there is no next frame for the old buffer.
        // In-flight snapshots still own their handles through the usual retirement gate.
        internal static void ReleaseReplacedBufferImages(TerminalBuffer buffer)
        {
            buffer.ReleaseImagesAfterSnapshots(Drain);

            void Drain()
            {
                var retired = new List<object>();
                // A replaced buffer has no upcoming frame to retry a transient disposal failure.
                if (DisposePass(retired))
                {
                    retired.Clear();
                    DisposePass(retired);
                }
            }

            bool DisposePass(List<object> retired)
            {
                if (!buffer.DrainRetiredImageHandles(retired, long.MaxValue)) return false;
                bool retry = false;
                foreach (var handle in retired)
                {
                    if (handle is not IDisposable disposable) continue;
                    try { disposable.Dispose(); }
                    catch { retry |= buffer.RetryRetiredImageDisposal(handle); }
                }
                return retry;
            }
        }

        // Non-zero while RenderOffscreen is rasterizing this view. UI thread only.
        private int _offscreenRenderDepth;

        /// <summary>
        /// Rasterizes this view into <paramref name="target"/> for a screenshot or export (the
        /// agent-host <c>live</c> capture, Export Snapshot (PNG)). UI thread only.
        /// </summary>
        /// <remarks>
        /// <see cref="Avalonia.Media.Imaging.RenderTargetBitmap.Render"/> calls <see cref="Render"/>
        /// and runs the draw operation synchronously on the UI thread, while the compositor's render
        /// thread may be drawing a live frame of the same buffer. So this pass must not act as a live
        /// frame. It takes an isolated snapshot, leaving the buffer's row-diff baseline to the live
        /// renderer. It also renders without the live row-picture cache: live frames repaint their
        /// dirty spans over the latest cached picture of each row, so that picture is the other half
        /// of the baseline, and the cache's disposal drain belongs to the render thread.
        /// </remarks>
        internal void RenderOffscreen(Avalonia.Media.Imaging.RenderTargetBitmap target)
        {
            ArgumentNullException.ThrowIfNull(target);
            Dispatcher.UIThread.VerifyAccess();

            _offscreenRenderDepth++;
            try
            {
                target.Render(this);
            }
            finally
            {
                _offscreenRenderDepth--;
            }
        }

        private IDisposable? _kittyAnimationWake;
        private long _kittyAnimationWakeDue;

        /// <summary>
        /// Moves running Kitty animations to the frame the clock is in before this frame snapshots the
        /// buffer, then arms one wake-up for the next change. Nothing is armed while no animation with
        /// a placement is running, so a still image costs no timer. Elapsed time, not the number of
        /// frames drawn, selects the frame.
        /// </summary>
        private void AdvanceKittyAnimations(TerminalBuffer buffer, bool schedule)
        {
            // Advancing is a pure function of the clock, so doing it for an offscreen capture (AgentHost live capture, PNG export) shows exactly what the
            // live view would show at that instant and consumes nothing a later live frame would not have consumed: the capture sees time, it changes none.
            // A buffer that never animates (or a held one) costs no lock and no clock read on this hot path.
            if (buffer.KittyAnimationPaused || !buffer.HasKittyAnimations) return;
            long now = buffer.KittyAnimationClock();
            // Only a placement in the rows on screen asks to be woken: an animated image scrolled into history costs no redraws.
            int firstVisibleLine;
            buffer.Lock.EnterReadLock();
            try { firstVisibleLine = Math.Max(0, buffer.InternalTotalLines - buffer.Rows - ScrollOffset); }
            finally { buffer.Lock.ExitReadLock(); }
            buffer.AdvanceKittyAnimations(now, out long? due, invalidate: false, firstVisibleLine: firstVisibleLine);
            // A retired view arms nothing, whatever a render it still receives finds running.
            if (!schedule || _retired || due is not { } at) return;
            if (_kittyAnimationWake is not null && _kittyAnimationWakeDue > now && _kittyAnimationWakeDue <= at) return;
            _kittyAnimationWake?.Dispose();
            _kittyAnimationWakeDue = at;
            _kittyAnimationWake = DispatcherTimer.RunOnce(() => OnKittyAnimationWake(), TimeSpan.FromMilliseconds(Math.Max(1, at - now)), DispatcherPriority.Render);
        }

        /// <summary>
        /// The animation's next change is due. A synchronized-output batch (<c>CSI ? 2026 h</c>) holds repaints until it ends, and the repaint it ends
        /// with arms the next wake, so nothing is requested while one is open. True when a repaint was requested.
        /// </summary>
        internal bool OnKittyAnimationWake()
        {
            _kittyAnimationWake = null;
            if (_retired || _buffer?.IsSynchronizedOutput == true) return false;
            InvalidateVisual();
            return true;
        }

        internal bool KittyAnimationWakeArmedForTest => _kittyAnimationWake is not null;

        private void CancelKittyAnimationWake()
        {
            _kittyAnimationWake?.Dispose();
            _kittyAnimationWake = null;
        }

        public override void Render(DrawingContext context)
        {
            var buffer = _buffer; // Capture local reference to prevent it becoming null mid-render (race condition)
            if (buffer == null)
            {
                // Absolute fallback without a buffer (so without the pane's theme): the app theme's
                // window background, so the view never flashes a fixed dark block on a light theme.
                context.FillRectangle(new SolidColorBrush(Ntilde.Shell.Theming.ThemeService.Current[Ntilde.Shell.Theming.ThemeKeys.WindowBg], _windowOpacity), new Rect(0, 0, Bounds.Width, Bounds.Height));
                return;
            }

            DisposeRetiredImageBitmaps();
            AdvanceKittyAnimations(buffer, schedule: _offscreenRenderDepth == 0);

            // Failsafe: Ensure we have fonts, but even if we don't, we should draw a background
            // to avoid "white panes"
            if (_glyphTypeface == null || _skTypeface == null)
            {
                // Fallback background fill if fonts aren't ready
                var theme = buffer.Theme;
                context.FillRectangle(new SolidColorBrush(theme.Background.ToAvaloniaColor(), _windowOpacity), new Rect(0, 0, Bounds.Width, Bounds.Height));
                return;
            }

            // Hide cursor if we are not focused or if blink mode currently hides it.
            bool hideCursor = !IsKeyboardFocusWithin;
            long nowTicks = DateTime.UtcNow.Ticks;

            // Snapshot state under lock to prevent race conditions during resize
            int snapshotRows, snapshotCols, totalLines, cursorRow, cursorCol;
            bool cursorVisibleMode, cursorBlinkMode, cursorSuppressedTemporarily;
            buffer.Lock.EnterReadLock();
            try
            {
                snapshotRows = this.Rows; // Use visual capacity, not buffer size
                snapshotCols = this.Cols;
                totalLines = buffer.InternalTotalLines;
                cursorRow = buffer.InternalCursorRow;
                cursorCol = buffer.InternalCursorCol;
                cursorVisibleMode = buffer.Modes.IsCursorVisible;
                cursorBlinkMode = ResolveCursorBlinkEnabled(buffer.Modes);
                cursorSuppressedTemporarily = buffer.CursorSuppressedUntilUtcTicks > nowTicks;
            }
            finally { buffer.Lock.ExitReadLock(); }

            if (!cursorVisibleMode) hideCursor = true;
            if (cursorBlinkMode && !_cursorBlinkPhase) hideCursor = true;
            if (cursorSuppressedTemporarily)
            {
                hideCursor = true;
                TerminalLogger.Debug($"[TerminalView] Render: Cursor suppressed temporarily.");
            }

            // Create and dispatch custom draw op
            var scaling = _cachedRenderScaling;
            if (Math.Abs(scaling - _lastRenderScalingForRowCache) > 0.0001)
            {
                _lastRenderScalingForRowCache = scaling;
                _renderCaches.Rows.RequestClear();
            }

            // See RenderOffscreen for why an off-screen pass must not look like a live frame.
            bool offscreen = _offscreenRenderDepth > 0;

            // The operation holds the caches until the compositor disposes it, possibly after this view is retired
            // (#644). A retired view drawn before it leaves the tree gets no lease, and draws without them.
            TerminalRenderCaches.Lease? cacheLease = _renderCaches.TryLease();

            context.Custom(new TerminalDrawOperation(
                Bounds,
                buffer,
                ScrollOffset,
                _selection,
                _searchMatches,
                _activeSearchIndex,
                _metrics,
                _typeface,
                _fontSize,
                _glyphTypeface!,
                _skTypeface,
                _skFont,
                _enableLigatures,
                _fallbackCache,
                FallbackChain.ToArray(),
                _windowOpacity,
                hideCursor,
                scaling,
                snapshotRows,
                snapshotCols,
                totalLines,
                cursorRow,
                cursorCol,
                offscreen ? null : cacheLease?.Rows,
                _enableComplexShaping,
                cacheLease?.Glyphs,
                _showRenderHud,
                isolatedSnapshot: offscreen,
                minimumContrast: _minimumContrast,
                cacheLease: cacheLease,
                requestImageFrame: offscreen ? null : () => Dispatcher.UIThread.Post(InvalidateVisual, DispatcherPriority.Render),
                preferredCursorStyle: _preferredCursorStyle,
                textBlinkVisible: _textBlink.ForegroundVisible,
                reportTextBlinkCells: offscreen ? null : _reportTextBlinkCells,
                nativePresenterDevicePixelsPerDip: NativeRasterPresenter?.DevicePixelsPerDip
            ));

            if (_hoveredLink != null)
            {
                int displayStart = Math.Max(0, totalLines - buffer.Rows - ScrollOffset);
                var brush = new SolidColorBrush(buffer.PresentedDefaultForeground.ToAvaloniaColor(), _windowOpacity);
                foreach (Rect underline in HoveredLinkUnderlines(displayStart, buffer.Rows))
                {
                    context.FillRectangle(brush, underline);
                }
            }

            if (_isBellFlashActive)
            {
                // The pane theme's foreground at a low alpha: a white flash was invisible on light themes.
                // As presented, so it still shows while the program has DECSCNM reverse video set.
                var flash = buffer.PresentedDefaultForeground.ToAvaloniaColor();
                context.FillRectangle(
                    new SolidColorBrush(Color.FromArgb(0x26, flash.R, flash.G, flash.B)),
                    new Rect(0, 0, Bounds.Width, Bounds.Height));
            }
        }
    }
}
