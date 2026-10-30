using Ntilde.VT;
using Ntilde.Rendering;

namespace Ntilde.Shell
{
    /// <summary>
    /// The render loop and the view's other timers: what each tick does, when they run, and stopping them for
    /// good when the pane is disposed.
    /// </summary>
    public partial class TerminalView
    {
        private void OnRenderTimerTick(object? sender, EventArgs e)
        {
            if (!_isDirty && _buffer?.IsSynchronizedOutput == true)
            {
                _buffer.FlushSynchronizedOutputTimeout();
            }

            if (_showRenderHud)
            {
                long now = DateTime.UtcNow.Ticks;
                if (TimeSpan.FromTicks(now - _lastHudUpdateTicks).TotalMilliseconds >= 100)
                {
                    _lastHudUpdateTicks = now;
                    _isDirty = true;
                }
            }

            if (_isDirty)
            {
                if (!_isUiRenderable)
                {
                    RendererStatistics.RecordHiddenInvalidationRequest();
                    return;
                }

                if (_buffer != null)
                {
                    int cursorRow, cursorCol;
                    long cursorSuppressedUntil;
                    _buffer.Lock.EnterReadLock();
                    try
                    {
                        cursorRow = _buffer.InternalCursorRow;
                        cursorCol = _buffer.InternalCursorCol;
                        cursorSuppressedUntil = _buffer.CursorSuppressedUntilUtcTicks;
                    }
                    finally { _buffer.Lock.ExitReadLock(); }

                    if (cursorRow != _lastCursorRow || cursorCol != _lastCursorCol)
                    {
                        _lastCursorRow = cursorRow;
                        _lastCursorCol = cursorCol;
                        CommandAssistAnchorHintChanged?.Invoke();

                        // Reset blink timer on VT cursor movement (like in Vim)
                        // Ensure we don't override the transient cursor suppression (used by AnsiParser for animated text)
                        long now = DateTime.UtcNow.Ticks;
                        if (_cursorBlinkEnabled && _cursorBlinkTimer.IsEnabled && cursorSuppressedUntil <= now)
                        {
                            TerminalLogger.Debug($"[TerminalView] OnRenderTimerTick: VT cursor moved ({_lastCursorRow},{_lastCursorCol}). Resetting blink phase.");
                            _cursorBlinkPhase = true;
                            _cursorBlinkTimer.Stop();
                            _cursorBlinkTimer.Start();
                        }
                        else if (cursorSuppressedUntil > now)
                        {
                            TerminalLogger.Debug($"[TerminalView] OnRenderTimerTick: VT cursor moved, but suppressed until {cursorSuppressedUntil} (now {now})");
                        }
                    }
                }

                _isDirty = false;
                InvalidateVisual();
            }
        }

        private void OnCursorBlinkTick(object? sender, EventArgs e)
        {
            if (!_cursorBlinkEnabled)
            {
                if (!_cursorBlinkPhase)
                {
                    _cursorBlinkPhase = true;
                    _isDirty = true;
                }
                return;
            }

            _cursorBlinkPhase = !_cursorBlinkPhase;
            _isDirty = true;
        }

        private void ResetCursorBlink()
        {
            if (!_cursorBlinkEnabled) return;

            // Block transient cursor suppression for 200ms to allow smooth TUI navigation
            _buffer?.BlockCursorSuppression(TimeSpan.FromMilliseconds(200));

            _cursorBlinkPhase = true;
            _cursorBlinkTimer.Stop();
            // Restart only if blink should be running at all: an unconditional Start here
            // would resurrect the timer on an unfocused pane and undo the #126 gating.
            if (ShouldRunCursorBlinkTimer())
            {
                _cursorBlinkTimer.Start();
            }
            _isDirty = true;
            InvalidateVisual();
        }

        private void OnMetricsTimerTick(object? sender, EventArgs e)
        {
            if (_buffer == null) return;

            var m = _buffer.GetMemoryMetrics(_renderCaches.Glyphs.EntryCount, GlyphCache.AtlasByteSize);

            // Format for easy log parsing/grep
            TerminalLogger.Debug(
                $"[TerminalMemory] " +
                $"ScrollbackMB={m.ScrollbackBytes / 1024.0 / 1024.0:F2} | " +
                $"Pages={m.ActivePages} (pooled={m.PooledPages}) | " +
                $"ViewportCells={m.ViewportCells} | " +
                $"GlyphCache={m.GlyphCacheEntries} entries ({m.GlyphCacheAtlasBytes / 1024.0 / 1024.0:F1} MB atlas)"
            );
        }

        /// <summary>The names of this view's timers that are running now.</summary>
        internal IReadOnlyList<string> RunningTimersForTest
        {
            get
            {
                var running = new List<string>();
                if (_renderTimer.IsEnabled) running.Add("render");
                if (_cursorBlinkTimer.IsEnabled) running.Add("cursor blink");
                if (_scrollAnimationTimer.IsEnabled) running.Add("scroll animation");
                if (_metricsTimer.IsEnabled) running.Add("metrics");
                if (_autoScrollTimer?.IsEnabled == true) running.Add("auto-scroll");
                if (_resizeThrottleTimer?.IsEnabled == true) running.Add("resize throttle");
                return running;
            }
        }

        private void StartUiTimers()
        {
            if (_uiTimersRunning) return;
            _renderTimer.Start();
            // Blink is gated separately from the render timer: output still arrives on an
            // unfocused pane and must render, but its cursor is not drawn at all.
            RefreshCursorBlinkTimerState();
            _uiTimersRunning = true;
            RendererStatistics.RecordTerminalViewTimersStarted();
        }

        /// The blink timer only earns its keep while the cursor is actually drawn.
        ///
        /// Render() already does `bool hideCursor = !IsKeyboardFocusWithin;`, so on an
        /// unfocused pane every blink tick set _isDirty, the render timer turned that into
        /// an InvalidateVisual, and the resulting frame was pixel-identical - a full render
        /// pass every 530 ms per unfocused pane, forever, for nothing (#126). With several
        /// panes open that is the dominant source of idle wakeups.
        ///
        /// Deliberately not folded into ShouldRunUiTimers(): that gates the render timer
        /// too, and stopping *that* on focus loss would freeze output from a background
        /// shell - a correctness bug, not a saving.
        private bool ShouldRunCursorBlinkTimer() =>
            !_retired && ShouldRunCursorBlinkTimer(_cursorBlinkEnabled, IsKeyboardFocusWithin);

        /// The rule itself, as a pure function so it can be tested without a visual tree or
        /// a focus manager. `focused` is the caller's IsKeyboardFocusWithin - the same input
        /// Render() uses for `hideCursor`, so the two cannot drift apart.
        internal static bool ShouldRunCursorBlinkTimer(bool blinkEnabled, bool focused) =>
            blinkEnabled && focused;

        private void RefreshCursorBlinkTimerState()
        {
            bool shouldBlink = ShouldRunCursorBlinkTimer();
            if (shouldBlink == _cursorBlinkTimer.IsEnabled)
            {
                return;
            }

            if (shouldBlink)
            {
                // Start solid so the cursor is immediately visible on focus, rather than
                // possibly resuming mid-blink in the hidden phase.
                _cursorBlinkPhase = true;
                _cursorBlinkTimer.Start();
            }
            else
            {
                _cursorBlinkTimer.Stop();
                // Leave the phase solid so a later focus gain cannot briefly show a hidden
                // cursor before the first tick.
                _cursorBlinkPhase = true;
            }
        }

        private void StopUiTimers()
        {
            if (!_uiTimersRunning) return;

            _renderTimer.Stop();
            _cursorBlinkTimer.Stop();
            _scrollAnimationTimer.Stop();
            // Nothing restarts this timer until the next wheel gesture, so a half-played
            // gesture must not linger as an in-flight target that blocks follow-output.
            _targetScrollOffset = _scrollOffset;
            _autoScrollTimer?.Stop();

            // A resize computed but not yet dispatched dies here, and nothing restarts this
            // timer - StartUiTimers deliberately does not. Remember that it happened so the
            // grid can be re-sent when the view becomes renderable again.
            if (_resizeThrottleTimer?.IsEnabled == true)
            {
                _resizeDispatchCancelled = true;

                // The latency clock dies with the dispatch it was timing. QueueResizeDispatch
                // only starts one when there is none, so leaving this set would bill the whole
                // hidden interval - seconds, or minutes - to the resize that eventually runs,
                // and RendererStatistics would report it as dispatch latency.
                _pendingResizeStartedAt = DateTime.MinValue;
            }

            _resizeThrottleTimer?.Stop();
            _uiTimersRunning = false;
            RendererStatistics.RecordTerminalViewTimersStopped();
        }

        // Set by Retire: the pane that owns this view was disposed, and no timer of this view may run again.
        private bool _retired;

        /// <summary>
        /// Stops every timer of this view for good, because its pane was disposed (#543). UI thread.
        /// </summary>
        /// <remarks>
        /// The timers otherwise stop only when the view leaves the visual tree, and a disposed pane can still be in it - a
        /// running timer would then keep the view, the pane and its window alive. Nothing starts one again afterwards:
        /// showing, focusing, scrolling or resizing the view.
        /// </remarks>
        internal void Retire()
        {
            _retired = true;
            _isUiRenderable = false;
            StopUiTimers();
            // Each of these can run without the render timer, and StopUiTimers stops nothing while that one is off.
            _cursorBlinkTimer.Stop();
            _scrollAnimationTimer.Stop();
            _autoScrollTimer?.Stop();
            _resizeThrottleTimer?.Stop();
            _metricsTimer.Stop();

            // And its glyph atlas and row pictures: the last draw operation still holding them disposes them (#644).
            _renderCaches.Release();
        }

        private bool ShouldRunUiTimers()
        {
            return !_retired &&
                   _isAttachedToVisualTree &&
                   IsVisible &&
                   IsEffectivelyVisible &&
                   Bounds.Width > 0 &&
                   Bounds.Height > 0;
        }

        private void RefreshUiTimerState()
        {
            bool shouldRun = ShouldRunUiTimers();
            _isUiRenderable = shouldRun;

            if (shouldRun)
            {
                StartUiTimers();
                if (_isDirty)
                {
                    InvalidateVisual();
                }
                return;
            }

            StopUiTimers();
        }
    }
}
