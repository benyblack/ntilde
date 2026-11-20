using Ntilde.Shell;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Replay;
using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Ntilde.UI.Replay
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "The playback token source is cancelled, not disposed: the playback loop may still read its token " +
                        "after playback stops, and a source with no timer or wait handle holds nothing to free.")]
    public partial class ReplayWindow : Window
    {
        private ReplayViewModel? _viewModel;
        private CancellationTokenSource? _playbackCts;
        private readonly PlaybackSessionGate _playbackSessionGate = new();
        private TerminalBuffer? _buffer;
        private AnsiParser? _parser;
        private readonly TerminalStateSnapshot? _initialReplayState;
        private string? _filePath;
        private bool _isUserScrubbing;
        private long _displayedTimeMs;
        internal Action? BeforeSeekRestoreForTest { get; set; }

        public ReplayWindow()
        {
            InitializeComponent();
            Ntilde.Shell.UiScale.FitWindow(this);
#if DEBUG
            TryAttachDeveloperTools(() => Application.Current?.AttachDeveloperTools());
#endif
        }

        internal static void TryAttachDeveloperToolsForTest(Action attach)
        {
            TryAttachDeveloperTools(attach);
        }

        private static void TryAttachDeveloperTools(Action? attach)
        {
            if (attach == null)
            {
                return;
            }

            try
            {
                attach();
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already been attached", StringComparison.OrdinalIgnoreCase))
            {
                // Secondary debug windows can be opened from an app that already attached the dev tools.
            }
        }

        /// <param name="theme">The app's active theme, for the replayed buffer's default colours;
        /// null keeps the Default theme.</param>
        public ReplayWindow(string filePath, TerminalTheme? theme = null) : this()
        {
            _filePath = filePath;
            _viewModel = new ReplayViewModel(filePath);
            DataContext = _viewModel;

            _buffer = new TerminalBuffer(80, 24); // Init with default
            if (theme != null)
            {
                // The buffer built its rows on the Default theme; remap them, as TerminalView.ApplySettings does.
                var defaultTheme = _buffer.Theme;
                _buffer.Theme = theme;
                _buffer.UpdateThemeColors(defaultTheme);
            }
            // A replay shows an animated Kitty image as its root frame and never plays it: the playhead follows a wall clock, while a recording has its
            // own time (pause, speed changes and seeks would leave it out of step), and the replay snapshots keep the root frame only.
            _buffer.KittyAnimationPaused = true;
            _parser = new AnsiParser(_buffer);
            // Replays of sessions that drew inline images must render the same way live panes do.
            _parser.ImageDecoder = new Ntilde.Rendering.SkiaImageDecoder();
            // Native-APC policy is deliberately left at the parser default (off): the recording
            // does not carry the setting the live pane ran with, and forcing it on would
            // fabricate inline images a policy-off session never displayed. Fail towards
            // hiding, not inventing: inline payloads recorded under an on-policy pane are
            // skipped at replay, while tunneled OSC 1339 payloads (never policy-gated) replay
            // as recorded. A future recording format that persists the effective policy can
            // initialize this from the file.
            // ReadFileBytes is likewise not wired: a t=f frame's payload is only a path, and
            // reading what sits there NOW would make replay depend on external filesystem
            // state instead of the recorded byte stream. t=f frames log-skip during playback.
            _initialReplayState = _buffer.ExportState(maxScrollbackRows: 0);
            _initialReplayState.Parser = _parser.ExportState();
            var termView = this.FindControl<TerminalView>("TermView");
            if (termView != null)
            {
                termView.SetBuffer(_buffer);
            }

            var btnPlayPause = this.FindControl<Button>("BtnPlayPause");
            if (btnPlayPause != null) btnPlayPause.Click += (s, e) => TogglePlay();

            var btnClose = this.FindControl<Button>("BtnClose");
            if (btnClose != null) btnClose.Click += (s, e) => Close();

            // Handle slider interaction
            var slider = this.FindControl<Slider>("TimelineSlider");
            if (slider != null)
            {
                // We use PointerPressed/Released to detect scrubbing
                slider.PointerPressed += (s, e) =>
                {
                    _isUserScrubbing = true;
                    if (_viewModel!.IsPlaying) StopPlayback();
                };

                slider.PointerReleased += (s, e) =>
                {
                    _isUserScrubbing = false;
                    _ = PerformSeek((long)slider.Value, CancellationToken.None);
                };

                // Also handle value changes (e.g. clicking on track)
                slider.PropertyChanged += (s, e) =>
                {
                    if (e.Property.Name == "Value" && _isUserScrubbing)
                    {
                        // Update time display while dragging, but don't seek heavy yet
                        _viewModel!.CurrentTimeMs = (long)(slider.Value);
                    }
                };
            }

            var comboSpeed = this.FindControl<ComboBox>("ComboSpeed");
            if (comboSpeed != null)
            {
                comboSpeed.SelectionChanged += (s, e) => ApplySelectedPlaybackSpeed(comboSpeed);
                ApplySelectedPlaybackSpeed(comboSpeed);
            }
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private void TogglePlay()
        {
            if (_viewModel == null) return;

            if (_viewModel.IsPlaying)
            {
                StopPlayback();
            }
            else
            {
                StartPlayback();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            StopPlayback();
            base.OnClosed(e);
            // Its glyph atlas and row pictures go once the last frame drawn with them does, as a closed pane's (#644).
            this.FindControl<TerminalView>("TermView")?.Retire();
        }

        private void StartPlayback()
        {
            if (_viewModel == null || _filePath == null) return;

            _playbackCts?.Cancel();
            _viewModel.IsPlaying = true;
            this.FindControl<Button>("BtnPlayPause")!.Content = "⏸";
            _playbackCts = new CancellationTokenSource();
            int sessionId = _playbackSessionGate.BeginSession();
            CancellationToken token = _playbackCts.Token;
            Task.Run(() => PlaybackLoop(sessionId, token));
        }

        private void StopPlayback()
        {
            if (_viewModel == null) return;

            _viewModel.IsPlaying = false;
            this.FindControl<Button>("BtnPlayPause")!.Content = "▶";
            _playbackSessionGate.InvalidateCurrentSession();
            _playbackCts?.Cancel();
            _playbackCts = null;
        }

        private async Task ApplyInSessionAsync(int sessionId, Action apply, CancellationToken cancellationToken = default)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested && _playbackSessionGate.IsCurrent(sessionId)) apply();
            }, DispatcherPriority.Normal, cancellationToken);
        }

        private Task ApplyHeaderMetricsAsync(int sessionId, ReplayHeader header, bool restoreHeader, CancellationToken cancellationToken) =>
            ApplyInSessionAsync(sessionId, () =>
            {
                if (restoreHeader && _parser is { } parser) ReplayRunner.ApplyHeaderMetrics(parser, header);
            }, cancellationToken);

        private Task ApplyCellMetricsAsync(int sessionId, float width, float height, CancellationToken cancellationToken) =>
            ApplyInSessionAsync(sessionId, () =>
            {
                if (_parser is { } parser) parser.SixelCellSizeOverride = new SixelCellSize(width, height);
            }, cancellationToken);

        private static bool HasValidSeekSnapshot(ReplaySnapshot? snapshot)
        {
            try
            {
                if (snapshot is not null) ReplayRunner.ValidateSnapshotCellLayout(snapshot);
                if (snapshot is not null && snapshot.State is not { Version: TerminalStateSnapshot.CurrentVersion })
                    TerminalBuffer.ValidateReplaySnapshot(snapshot);
                return true;
            }
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException) { return false; }
        }

        private async Task PerformSeek(long targetTimeMs, CancellationToken cancellationToken = default)
        {
            if (_viewModel == null || _buffer == null || _filePath == null || _parser == null) return;
            StopPlayback();
            int sessionId = _playbackSessionGate.BeginSession();
            var nearest = _viewModel.GetNearestSnapshotEntry(targetTimeMs);
            if (!HasValidSeekSnapshot(nearest?.Snapshot))
            {
                _viewModel.CurrentTimeMs = _displayedTimeMs;
                return;
            }

            var snapEntry = _viewModel.GetNearestSeekCheckpoint(targetTimeMs);
            var output = new ReplayOutput(snapEntry?.Snapshot);
            long appliedTime = _displayedTimeMs;
            bool restored = false;

            bool reachedTarget = false;
            using var seekCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            seekCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var runner = new ReplayReader(_filePath);
                var result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        return ApplyInSessionAsync(sessionId, () => output.Apply(_parser, data), seekCts.Token);
                    },
                    onHeaderCallback: header => ApplyHeaderMetricsAsync(sessionId, header, snapEntry is null, seekCts.Token),
                    onSixelCellSizeCallback: (w, h) => ApplyCellMetricsAsync(sessionId, w, h, seekCts.Token),
                    onResizeCallback: (w, h) => ApplyInSessionAsync(sessionId, () => _buffer.Resize(w, h), seekCts.Token),
                    onTimeUpdate: time => { appliedTime = Math.Max(appliedTime, time); return Task.CompletedTask; },
                    options: new ReplayRunOptions
                    {
                        StopAtTimeMs = targetTimeMs,
                        ApplyHeaderResize = snapEntry is null,
                        StartAfterLineNumber = snapEntry?.LineNumber ?? 0,
                        OnValidatedStart = async token =>
                        {
                            restored = await ApplySeekSnapshotAsync(sessionId, snapEntry?.Snapshot, snapEntry?.TimeMs ?? 0, token);
                            if (restored) appliedTime = snapEntry?.TimeMs ?? 0;
                            return restored;
                        },
                        OnSkippedData = output.ObserveSkipped
                    },
                    minTimeMs: snapEntry?.TimeMs ?? 0,
                    ct: seekCts.Token);
                if (!restored) return;
                appliedTime = Math.Max(appliedTime, result.LastGoodOffset);
                reachedTarget = !seekCts.IsCancellationRequested && !result.Truncated;
            }
            catch (OperationCanceledException) when (seekCts.IsCancellationRequested) { /* Keep the last applied seek position. */ }
            catch (Exception ex) { TerminalLogger.Error($"[ReplayWindow] Seek Error: {ex.Message}"); }
            // Publish the last applied position even after cancellation; the session gate
            // still prevents a superseded run from updating the current display.
            await ApplyInSessionAsync(sessionId, () =>
            {
                _displayedTimeMs = reachedTarget ? targetTimeMs : appliedTime;
                _viewModel.CurrentTimeMs = _displayedTimeMs;
            }, CancellationToken.None);
        }

        private async Task<bool> ApplySeekSnapshotAsync(int sessionId, ReplaySnapshot? snapshot, long appliedTime, CancellationToken cancellationToken)
        {
            var buffer = _buffer;
            var parser = _parser;
            var viewModel = _viewModel;
            var initialState = _initialReplayState;
            if (buffer is null || parser is null || viewModel is null) return false;
            bool applied = false;
            try
            {
                BeforeSeekRestoreForTest?.Invoke();
                await ApplyInSessionAsync(sessionId, () =>
                {
                    if (snapshot is not null) ReplayRunner.ApplySnapshot(buffer, parser, snapshot);
                    else if (initialState is not null) TerminalStateTransfer.Restore(buffer, parser, initialState);
                    else return;
                    _displayedTimeMs = appliedTime;
                    viewModel.CurrentTimeMs = appliedTime;
                    applied = true;
                }, cancellationToken);
                return applied;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[ReplayWindow] Snapshot Error: {ex.Message}");
                await ApplyInSessionAsync(sessionId, () => viewModel.CurrentTimeMs = _displayedTimeMs, CancellationToken.None);
                return false;
            }
        }

        private async Task PlaybackLoop(int sessionId, CancellationToken ct)
        {
            if (_filePath == null || _viewModel == null || _buffer == null || _parser == null) return;

            try
            {
                var runner = new ReplayReader(_filePath);
                long startTimeMs = _viewModel.CurrentTimeMs;

                var nearest = _viewModel.GetNearestSnapshotEntry(startTimeMs);
                if (!HasValidSeekSnapshot(nearest?.Snapshot))
                {
                    await ApplyInSessionAsync(sessionId, () => _viewModel.CurrentTimeMs = _displayedTimeMs, CancellationToken.None);
                    return;
                }
                var snapEntry = _viewModel.GetNearestSeekCheckpoint(startTimeMs);
                var output = new ReplayOutput(snapEntry?.Snapshot);
                long minTime = snapEntry?.TimeMs ?? 0;

                await runner.RunWithResultAsync(
                    onDataCallback: async (data) =>
                    {
                        await ApplyInSessionAsync(sessionId, () => output.Apply(_parser, data), ct);
                    },
                    onHeaderCallback: header => ApplyHeaderMetricsAsync(sessionId, header, snapEntry is null, ct),
                    onSixelCellSizeCallback: (w, h) => ApplyCellMetricsAsync(sessionId, w, h, ct),
                    onResizeCallback: async (w, h) =>
                    {
                        await ApplyInSessionAsync(sessionId, () => _buffer.Resize(w, h), ct);
                    },
                    // We don't apply snapshots during playback usually, or maybe we do to self-correct?
                    // For now ignore snapshots in stream during playback to avoid jitter
                    onSnapshotCallback: null,
                    onTimeUpdate: async (timeMs) =>
                    {
                        if (_isUserScrubbing) return;
                        // Throttle UI updates or just post
                        await Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            if (!_isUserScrubbing && _playbackSessionGate.IsCurrent(sessionId))
                            {
                                _displayedTimeMs = Math.Max(startTimeMs, timeMs);
                                _viewModel.CurrentTimeMs = _displayedTimeMs;
                            }
                        });
                    },
                    options: new ReplayRunOptions
                    {
                        PlaybackMode = ReplayPlaybackMode.Realtime,
                        ApplyHeaderResize = snapEntry is null,
                        StartAfterLineNumber = snapEntry?.LineNumber ?? 0,
                        OnValidatedStart = token => ApplySeekSnapshotAsync(sessionId, snapEntry?.Snapshot, startTimeMs, token),
                        OnSkippedData = output.ObserveSkipped
                    },
                    minTimeMs: minTime,
                    fastForwardToMs: startTimeMs,
                    playbackSpeed: _viewModel.PlaybackSpeed,
                    ct: ct
                );
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                TerminalLogger.Error($"[ReplayWindow] Playback Error: {ex.Message}");
            }
            finally
            {
                Dispatcher.UIThread.Post(() => StopPlaybackIfCurrent(sessionId));
            }
        }

        // Older checkpoints omitted the decoder tail. Observe the skipped prefix
        // with its original UTF-8 decoding policy, then seed the parser once.
        private sealed class ReplayOutput(ReplaySnapshot? snapshot)
        {
            private Ntilde.Pty.Utf8ChunkDecoder? _legacy = snapshot is { HasCapturedDecoderTail: false } ? new() : null;

            public void ObserveSkipped(byte[] data)
            {
                if (_legacy is not { } decoder) return;
                var chars = System.Buffers.ArrayPool<char>.Shared.Rent(Ntilde.Pty.Utf8ChunkDecoder.GetMaxCharCount(data.Length));
                try { decoder.Decode(data, chars); }
                finally { System.Buffers.ArrayPool<char>.Shared.Return(chars); }
            }

            public void Apply(AnsiParser parser, byte[] data)
            {
                if (_legacy is { } decoder)
                {
                    parser.RestoreOutputTail(decoder.PendingTail);
                    _legacy = null;
                }
                parser.ProcessBytes(data);
            }
        }

        private void StopPlaybackIfCurrent(int sessionId)
        {
            if (_playbackSessionGate.IsCurrent(sessionId)) StopPlayback();
        }

        private void ApplySelectedPlaybackSpeed(ComboBox comboSpeed)
        {
            if (_viewModel == null) return;
            if (comboSpeed.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string tag) return;

            if (double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) && speed > 0)
            {
                _viewModel.PlaybackSpeed = speed;
            }
        }
    }
}
