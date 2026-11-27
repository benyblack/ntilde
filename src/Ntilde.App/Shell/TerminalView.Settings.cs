using Avalonia.Media;
using Ntilde.VT;

namespace Ntilde.Shell
{
    /// <summary>
    /// Applying TerminalSettings to the view.
    /// </summary>
    public partial class TerminalView
    {
        public void ApplySettings(TerminalSettings settings)
        {
            try
            {
                // Check if font properties changed to avoid unnecessary Skia recreation (prevents crash on rapid opacity changes)
                bool fontChanged = Math.Abs(_fontSize - settings.FontSize) > 0.01 ||
                                   (_typeface.FontFamily.Name != settings.FontFamily);

                _fontSize = settings.FontSize;
                if (fontChanged)
                {
                    _typeface = new Typeface(settings.FontFamily);
                }
                // Ligatures, shaping and opacity are baked into the cached row pictures.
                bool rowPicturesStale = settings.EnableLigatures != _enableLigatures ||
                                        settings.EnableComplexShaping != _enableComplexShaping ||
                                        Math.Abs(settings.WindowOpacity - _windowOpacity) > 0.0001;
                _enableLigatures = settings.EnableLigatures;
                _enableComplexShaping = settings.EnableComplexShaping;
                double minimumContrast = Math.Clamp(double.IsNaN(settings.MinimumContrast) ? 1.0 : settings.MinimumContrast, 1.0, 21.0);
                if (Math.Abs(minimumContrast - _minimumContrast) > 0.001)
                {
                    _minimumContrast = minimumContrast;
                    // Cached row pictures hold their colors baked in.
                    _renderCaches.Rows.RequestClear();
                }
                _windowOpacity = settings.WindowOpacity;
                _cursorBlinkEnabled = settings.CursorBlink;
                _preferredCursorStyle = ParseCursorStyle(settings.CursorStyle);
                _bellAudioEnabled = settings.BellAudioEnabled;
                _bellVisualEnabled = settings.BellVisualEnabled;
                if (!_bellVisualEnabled && IsBellFlashActive) EndVisualFlash();
                _enableSmoothScrolling = settings.SmoothScrolling;
                _enableLinkDetection = settings.EnableLinkDetection;
                _enableKittyKeyboardProtocol = settings.EnableKittyKeyboardProtocol;
                if (!_enableKittyKeyboardProtocol) ResetKittyKeyTracking(clearPendingText: false);
                // Fall back to the default for non-positive/NaN values; clamp the upper
                // bound so a wild settings value can't drive runaway scroll steps.
                double wheelLinesPerNotch = settings.WheelLinesPerNotch;
                if (double.IsNaN(wheelLinesPerNotch) || wheelLinesPerNotch <= 0)
                {
                    wheelLinesPerNotch = 3.0;
                }
                _wheelLinesPerNotch = Math.Min(wheelLinesPerNotch, 100.0);
                // Re-evaluate the effective default/override policy immediately when the host
                // setting changes, rather than waiting for focus or terminal output.
                RefreshCursorBlinkTimerState();
                EnsureFallbackChain();

                if (_buffer != null)
                {
                    _buffer.MaxHistory = settings.MaxHistory;
                    _buffer.EnterBatchWrite();
                    try
                    {
                        if (_buffer.Modes.CursorStyleIsDefault)
                        {
                            _buffer.Modes.CursorStyle = _preferredCursorStyle;
                            _buffer.Modes.IsCursorBlinkEnabled = settings.CursorBlink;
                        }
                    }
                    finally { _buffer.ExitBatchWrite(); }

                    // Every settings save lands here, most of them with the theme unchanged; re-applying
                    // it anyway remaps every cell and repaints every row in every pane. The copy catches
                    // a theme edited in place, which leaves _buffer.Theme the very instance it compares to.
                    var newTheme = settings.ActiveTheme;
                    bool themeChanged = !_buffer.Theme.HasSameColors(newTheme) ||
                                        _appliedTheme?.HasSameColors(newTheme) != true;
                    if (themeChanged)
                    {
                        // Store old theme for color remapping
                        var oldTheme = _buffer.Theme;

                        // Apply new theme
                        _buffer.Theme = newTheme;
                        _appliedTheme = newTheme.Clone();

                        // Clear row cache as colors are now baked into SKPictures
                        _renderCaches.Rows.RequestClear();

                        // Update all existing cells, remapping old theme colors to new
                        _buffer.UpdateThemeColors(oldTheme);

                        TerminalLogger.Log(
                            $"[TerminalView] Theme applied: '{_buffer.Theme.Name}' (was '{oldTheme.Name}'); " +
                            $"buffer {_buffer.Cols}x{_buffer.Rows}.");

                        // Force immediate visual refresh
                        InvalidateVisual();
                    }
                    else if (rowPicturesStale)
                    {
                        _renderCaches.Rows.RequestClear();
                        InvalidateVisual();
                    }
                }

                // Only recreate resources if font changed
                if (fontChanged)
                {
                    MeasureCharSize();

                    // Trigger resize based on new font metrics and current bounds
                    // BUT only if dimensions actually changed (to avoid overwriting theme-updated cells)
                    if (_buffer != null && TryComputeGrid(Bounds.Size, _metrics.CellWidth, _metrics.CellHeight, out int cols, out int rows))
                    {
                        bool fontGridChanged = DefersBufferResizeToSession
                            ? GridDiffersFromLastSent(cols, rows)
                            : (cols != _buffer.Cols || rows != _buffer.Rows);

                        if (fontGridChanged)
                        {
                            LastCompletedResizeDispatch = default;
                            if (!DefersBufferResizeToSession) _buffer.Resize(cols, rows);
                            ResetMouseMotionTracking(); // Issue #269: grid reflowed, cell coords are stale.
                            var resizeTarget = (_session, _buffer);
                            OnResize?.Invoke(cols, rows);

                            // The third place a dispatch happens, and the one easiest to miss:
                            // a font change re-grids the buffer and the PTY straight from the
                            // new metrics, bypassing the throttle entirely. Leaving it
                            // unrecorded would make the field describe a grid that had since
                            // been superseded, which is the same defect this change is about.
                            RecordGridSent(cols, rows);
                            CompleteResizeDispatch(cols, rows, resizeTarget);
                        }
                    }
                }

                InvalidateVisual();
            }
            catch (Exception ex)
            {
                TerminalLogger.Error("ApplySettings failed. " + ex);
            }
        }

        private static CursorStyle ParseCursorStyle(string? style)
        {
            if (string.IsNullOrWhiteSpace(style)) return CursorStyle.Underline;

            if (Enum.TryParse<CursorStyle>(style, true, out var parsed))
            {
                return parsed;
            }

            return style.Trim().ToLowerInvariant() switch
            {
                "bar" => CursorStyle.Beam,
                "beam" => CursorStyle.Beam,
                "block" => CursorStyle.Block,
                "underline" => CursorStyle.Underline,
                _ => CursorStyle.Underline
            };
        }
    }
}
