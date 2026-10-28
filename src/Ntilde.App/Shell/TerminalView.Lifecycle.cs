using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Ntilde.Shell
{
    /// <summary>
    /// Joining and leaving the visual tree, render scaling, visibility and focus changes, and releasing Skia
    /// resources.
    /// </summary>
    public partial class TerminalView
    {
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _isAttachedToVisualTree = true;
            RefreshUiTimerState();
            if (!_retired && !_metricsTimer.IsEnabled) _metricsTimer.Start();

            _cachedTopLevel = TopLevel.GetTopLevel(this);
            if (_cachedTopLevel != null)
            {
                _cachedTopLevel.ScalingChanged += OnTopLevelScalingChanged;
            }
            // The interface scale in effect for THIS view comes from the resource chain (a window
            // may hold or be reduced to a scale other than the app's), and every change to it -
            // UiScale.Apply swapping the app resource, a window pinning or releasing its own -
            // arrives as ResourcesChanged on this control. No static event, so nothing can reach a
            // view that a previous headless test's application left behind.
            ResourcesChanged += OnScaleResourcesChanged;
            _cachedRenderScaling = ComputeEffectiveRenderScaling();

            // Ensure char metrics are available immediately upon attachment
            MeasureCharSize();

            // Deliberately here and not in RefreshUiTimerState above, which is where the
            // renderable/not decision is made. Re-attaching can land the view on a top level
            // with a different render scale, and the cell metrics for it are not known until
            // the MeasureCharSize on the line above - reconciling before that point would
            // compute the grid from the old monitor and dispatch it, and if the new bounds
            // happen to match in DIPs no later OnSizeChanged would ever correct it.
            ReconcileDispatchedGrid();

            _isDirty = true;
            if (_isUiRenderable)
            {
                InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _isAttachedToVisualTree = false;
            _isUiRenderable = false;
            StopUiTimers();
            _metricsTimer.Stop();

            if (_cachedTopLevel != null)
            {
                _cachedTopLevel.ScalingChanged -= OnTopLevelScalingChanged;
                _cachedTopLevel = null;
            }
            ResourcesChanged -= OnScaleResourcesChanged;
        }

        /// <summary>
        /// Device pixels per DIP of THIS control: the monitor's render scale times the interface
        /// scale in effect for this view. The glyph atlas, the pixel grid and the row cache all key
        /// off this number, so it has to describe the density the sprites actually land at - the
        /// Window theme's layout transform sits between this control and the surface, and a sprite
        /// rasterized at the monitor scale alone comes out upscaled and blurry under 150%. The
        /// scale is the transform resource the theme binds (<see cref="UiScale.TransformResourceKey"/>)
        /// resolved from this view's own position in the tree, so a window that holds or was
        /// reduced to another scale (Settings, a Replay window the screen could not fit) gets an
        /// atlas for the scale it really renders at rather than for <see cref="UiScale.Current"/>.
        /// </summary>
        private double ComputeEffectiveRenderScaling()
        {
            double interfaceScale = UiScale.Current;
            if (this.TryFindResource(UiScale.TransformResourceKey, out object? resource)
                && resource is ScaleTransform transform
                && transform.ScaleX > 0)
            {
                interfaceScale = transform.ScaleX;
            }

            return (_cachedTopLevel?.RenderScaling ?? 1.0) * interfaceScale;
        }

        /// <summary>
        /// Device pixels per DIP this view currently renders at: monitor scale times interface
        /// scale. Anything that reports the terminal's size in pixels (the kitty mode-2048 in-band
        /// resize report) must multiply by this, not by the monitor scale alone.
        /// </summary>
        internal double EffectiveRenderScaling => _cachedRenderScaling;

        private void OnTopLevelScalingChanged(object? sender, EventArgs e)
        {
            _cachedRenderScaling = ComputeEffectiveRenderScaling();
            MeasureCharSize();
        }

        private void OnScaleResourcesChanged(object? sender, ResourcesChangedEventArgs e)
        {
            double next = ComputeEffectiveRenderScaling();
            if (Math.Abs(next - _cachedRenderScaling) < 0.0001)
            {
                return;
            }

            _cachedRenderScaling = next;
            MeasureCharSize();
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty || change.Property == BoundsProperty)
            {
                RefreshUiTimerState();

                // A dispatch cancelled when this view stopped being renderable is re-sent when
                // it starts again (#432). Metrics are already current on this path - nothing
                // here changes the top level - so unlike the attach path it can follow the
                // renderable decision directly.
                ReconcileDispatchedGrid();
            }
            else if (change.Property == IsKeyboardFocusWithinProperty)
            {
                // Tracked on IsKeyboardFocusWithin rather than in OnGotFocus/OnLostFocus so
                // the timer follows exactly the same condition Render() uses to decide
                // whether to draw the cursor - including focus landing on a descendant,
                // which the OnGotFocus/OnLostFocus pair does not observe.
                RefreshCursorBlinkTimerState();
            }
        }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            string? sequence = TerminalInputModeEncoder.EncodeFocusChanged(_buffer?.Modes, isFocused: true);
            if (_session != null && sequence != null)
            {
                _session.SendInput(sequence);
            }
            _isDirty = true;
            InvalidateVisual();
        }

        protected override void OnLostFocus(FocusChangedEventArgs e)
        {
            base.OnLostFocus(e);
            string? sequence = TerminalInputModeEncoder.EncodeFocusChanged(_buffer?.Modes, isFocused: false);
            if (_session != null && sequence != null)
            {
                _session.SendInput(sequence);
            }
            _isDirty = true;
            InvalidateVisual();
        }


        private void ClearSkiaResources()
        {
            _skFont?.Dispose();
            _skFont = null;
            _skTypeface?.Dispose();
            _skTypeface = null;

            _fallbackCache.Clear();
            _rowCache.RequestClear();
            _glyphCache.Clear();
        }
    }
}
