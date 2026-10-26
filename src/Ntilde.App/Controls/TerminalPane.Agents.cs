using Ntilde.Shell;
using Avalonia.Controls;
using Avalonia;
using Avalonia.Platform.Storage;

namespace Ntilde.Controls
{
    /// <summary>
    /// What the agent host sees of a pane: its session snapshot and render parameters, live capture and
    /// snapshot export, and the agent output panel.
    /// </summary>
    public partial class TerminalPane
    {
        // Agent Output panel state, created in SetupCommon alongside the other pane-lifetime
        // services. The tracker owns the debounced region reads; the view model owns visibility
        // (user toggle minus alt-screen suppression); the host is the lazily-created view.
        private AgentOutput.AgentOutputViewModel? _agentOutput;
        private AgentOutput.AgentOutputRegionTracker? _agentOutputTracker;
        private AgentOutput.AgentOutputPanel? _agentOutputPanelHost;

        /// <summary>
        /// Pushes this pane's current metadata into its agent-session
        /// registration (UI thread only). Cheap and allocation-light; called
        /// on title/cwd/profile/active-state changes so background registry
        /// readers never touch this control.
        /// </summary>
        private void UpdateAgentSessionSnapshot()
        {
            _agentRegistration?.UpdateSnapshot(
                GetBaseTabTitle(),
                Profile?.Name ?? "Terminal",
                Profile?.Type == ConnectionType.SSH ? "ssh" : "local",
                IsActivePane,
                Profile?.Id);
        }

        /// <summary>
        /// Pushes this pane's render inputs (cell metrics, font, shaping flags)
        /// into its agent-session registration so <c>captureScreen</c> can render
        /// the pane off the UI thread (A5). UI thread only; called whenever the
        /// font is re-measured or settings are applied.
        /// </summary>
        private void UpdateAgentRenderParameters()
        {
            if (_agentRegistration == null) return;

            _agentRegistration.UpdateRenderParameters(new Ntilde.Shell.PaneRenderParameters(
                TermView.Metrics,
                TermView.Typeface.FontFamily.Name,
                (float)TermView.FontSize,
                TermView.EnableLigatures,
                TermView.EnableComplexShaping));
        }

        // ------------------------------------------------------------- Agent Output panel
        //
        // The markdown side panel: a per-pane, manually toggled view over the current command's
        // output region. It reads the grid, it never writes it - the terminal core is untouched,
        // and the whole feature turns off the moment the user closes the panel.

        /// <summary>
        /// The toggle flipped. Creates the panel view on first open; the tracker only reads the
        /// grid while the panel is open.
        /// </summary>
        private void SetAgentOutputPanelOpen(bool open)
        {
            if (_agentOutput == null)
            {
                return;
            }

            _agentOutput.IsPanelOpen = open;
            if (open)
            {
                EnsureAgentOutputPanelHost();
                _agentOutputTracker?.SetEnabled(true);

                // A command that finished while the panel was closed never delivered its final
                // update (D skips the read for a hidden panel), so the reopened view model may
                // still claim the old output is streaming. Clear that first; if a live region
                // does exist, the flush below immediately re-marks it as streaming.
                _agentOutput.ClearStreaming();

                // Fallback on: nothing is tracked when the panel opens onto output that already
                // finished (no live C mark, no Enter-time heuristic yet), and the panel should
                // show that output as a recent-tail snapshot rather than an empty state.
                _agentOutputTracker?.FlushNow(includeRecentTailFallback: true);
            }
            else
            {
                _agentOutputTracker?.SetEnabled(false);
            }

            UpdateAgentOutputPanelVisibility();
        }

        private void UpdateAgentOutputPanelVisibility()
        {
            if (_agentOutput != null)
            {
                AgentOutputPanelPresenter.IsVisible = _agentOutput.IsShown;
            }
        }

        /// <summary>Lazily builds the panel view, mirroring EnsureRemoteFilesSidebarHost.</summary>
        private void EnsureAgentOutputPanelHost()
        {
            if (_agentOutputPanelHost != null || _agentOutput == null)
            {
                return;
            }

            var host = new AgentOutput.AgentOutputPanel();
            host.SetViewModel(_agentOutput);
            host.CloseRequested += () => AgentOutputToggle.IsChecked = false;
            AgentOutputPanelPresenter.Content = host;
            _agentOutputPanelHost = host;
        }

        /// <summary>
        /// Enter observed with shell integration inactive: pin the output-region start from the
        /// cursor, the markless fallback. Without this, sessions without shell integration have
        /// no region to track at all. Runs after <see cref="OnCommandAssistEnterObserving"/> and,
        /// like it, before the carriage return reaches the PTY: this records a grid row, and the row
        /// only means what it says while the line the user submitted is still the one on screen.
        /// </summary>
        private void OnAgentOutputEnterObserved()
        {
            // Not gated on the panel being open: the row is only answerable at Enter, and a panel
            // opened later needs it to bound the response instead of guessing at prompt shapes.
            if (_isShellIntegrationActive)
            {
                return;
            }

            _agentOutputTracker?.CaptureHeuristicStart();
        }

        /// <summary>
        /// Photographs this pane's live control for the agent-host <c>live</c>
        /// capture mode (A5). UI thread only. Null when the pane has never been
        /// laid out, or the image would exceed the per-capture pixel budget.
        /// </summary>
        /// <remarks>
        /// Deliberately the on-screen control rather than a buffer re-render: this
        /// is the mode that exists to carry what the headless path structurally
        /// cannot — the user's background image and window opacity, which the draw
        /// operation never paints. It costs reproducibility (the same session
        /// photographs differently as the window moves, resizes, or is covered) and
        /// only works while the pane is on screen, which is why <c>render</c> stays
        /// the default.
        ///
        /// <paramref name="scale"/> is applied as the render DPI, the same knob the
        /// manual PNG export takes from <c>TopLevel.RenderScaling</c> — but named by
        /// the caller here, so a live capture does not silently change resolution
        /// when the window moves between monitors.
        /// </remarks>
        internal Ntilde.AgentHost.AgentLiveCapture? CaptureLiveForAgent(int maxWidth, double scale)
        {
            var view = TermView;
            if (view == null) return null;

            double width = view.Bounds.Width;
            double height = view.Bounds.Height;
            if (width <= 0 || height <= 0)
            {
                // Never laid out (a pane in a background tab that has not been
                // shown yet), so there are no on-screen pixels to photograph.
                return null;
            }

            double effectiveScale = scale <= 0
                ? 1.0
                : Math.Min(scale, Ntilde.AgentHost.Contracts.AgentHostProtocol.MaxCaptureScale);
            var pixelSize = new PixelSize(
                (int)Math.Ceiling(width * effectiveScale),
                (int)Math.Ceiling(height * effectiveScale));
            if (pixelSize.Width <= 0 || pixelSize.Height <= 0) return null;
            if ((long)pixelSize.Width * pixelSize.Height > Ntilde.AgentHost.Contracts.AgentHostProtocol.MaxCapturePixels)
            {
                return null;
            }

            try
            {
                byte[] png;
                using (var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(
                    pixelSize, new Vector(96 * effectiveScale, 96 * effectiveScale)))
                {
                    // Not rtb.Render(view): this runs beside the render thread's live frames,
                    // and RenderOffscreen keeps it from acting as one.
                    view.RenderOffscreen(rtb);
                    using var stream = new System.IO.MemoryStream();
                    rtb.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                    png = stream.ToArray();
                }

                if (maxWidth <= 0 || pixelSize.Width <= maxWidth)
                {
                    return new Ntilde.AgentHost.AgentLiveCapture(png, pixelSize.Width, pixelSize.Height, Downscaled: false);
                }

                // Round-tripping through Skia to resample: RenderTargetBitmap gives
                // an Avalonia bitmap, and the one resampler in the product that is
                // pinned to fixed sampling lives in TerminalSnapshotRenderer. A
                // screenshot is rare enough that the extra decode is not worth a
                // second scaling path.
                using var decoded = SkiaSharp.SKBitmap.Decode(png);
                if (decoded == null)
                {
                    return new Ntilde.AgentHost.AgentLiveCapture(png, pixelSize.Width, pixelSize.Height, Downscaled: false);
                }
                using var resized = Ntilde.Shell.TerminalSnapshotRenderer.DownscaleToWidth(decoded, maxWidth);
                if (resized == null)
                {
                    return new Ntilde.AgentHost.AgentLiveCapture(png, pixelSize.Width, pixelSize.Height, Downscaled: false);
                }
                return new Ntilde.AgentHost.AgentLiveCapture(
                    Ntilde.Shell.TerminalSnapshotRenderer.EncodePng(resized),
                    resized.Width,
                    resized.Height,
                    Downscaled: true);
            }
            catch (Exception ex)
            {
                // A capture is a read: a failed one must never take down the pane
                // or the endpoint.
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] live agent capture failed: {ex}");
                return null;
            }
        }

        public async Task ExportSnapshotAsync(string format)
        {
            if (Buffer == null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.StorageProvider == null) return;

            string ext = format.ToLowerInvariant() switch
            {
                "png" => ".png",
                "ansi" => ".ansi",
                _ => ".txt"
            };

            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = $"Export Terminal Snapshot ({format.ToUpperInvariant()})",
                SuggestedFileName = $"snapshot_{DateTime.Now:yyyyMMdd_HHmmss}{ext}"
            });

            if (file == null) return;

            try
            {
                if (format.Equals("png", StringComparison.OrdinalIgnoreCase))
                {
                    var dpi = topLevel.RenderScaling;
                    var pixelSize = new PixelSize(
                        (int)Math.Ceiling(TermView.Bounds.Width * dpi),
                        (int)Math.Ceiling(TermView.Bounds.Height * dpi));

                    var rtb = new Avalonia.Media.Imaging.RenderTargetBitmap(pixelSize, new Vector(96 * dpi, 96 * dpi));
                    TermView.RenderOffscreen(rtb);

                    using var stream = await file.OpenWriteAsync();
                    rtb.Save(stream, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                else if (format.Equals("ansi", StringComparison.OrdinalIgnoreCase))
                {
                    string data = Ntilde.VT.Export.TerminalExporter.ExportToAnsi(Buffer);
                    using var stream = await file.OpenWriteAsync();
                    using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(data);
                }
                else
                {
                    string data = Ntilde.VT.Export.TerminalExporter.ExportToPlainText(Buffer);
                    using var stream = await file.OpenWriteAsync();
                    using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
                    await writer.WriteAsync(data);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[TerminalPane] Failed to export snapshot: {ex}");
            }
        }
    }
}
