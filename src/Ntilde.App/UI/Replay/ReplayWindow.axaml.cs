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
        private string? _filePath;
        private bool _isUserScrubbing;
        private long _displayedTimeMs;

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
                    _ = PerformSeek((long)slider.Value);
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

        private async Task ApplyInSessionAsync(int sessionId, Action apply)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_playbackSessionGate.IsCurrent(sessionId)) apply();
            });
        }

        private Task ApplyHeaderMetricsAsync(int sessionId, ReplayHeader header, bool restoreHeader) =>
            ApplyInSessionAsync(sessionId, () =>
            {
                if (restoreHeader && _parser is { } parser) ReplayRunner.ApplyHeaderMetrics(parser, header);
            });

        private Task ApplyCellMetricsAsync(int sessionId, float width, float height) =>
            ApplyInSessionAsync(sessionId, () =>
            {
                if (_parser is { } parser) parser.SixelCellSizeOverride = new SixelCellSize(width, height);
            });

        private static bool HasValidSeekMetrics(ReplaySnapshot? snapshot)
        {
            try { _ = RecordingCellMetrics.FromOptional(snapshot?.SixelCellWidthPx, snapshot?.SixelCellHeightPx); return true; }
            catch (InvalidDataException) { return false; }
        }

        private async Task PerformSeek(long targetTimeMs, CancellationToken cancellationToken = default)
        {
            if (_viewModel == null || _buffer == null || _filePath == null || _parser == null) return;
            StopPlayback();
            int sessionId = _playbackSessionGate.BeginSession();
            var snapEntry = _viewModel.GetNearestSnapshotEntry(targetTimeMs);
            if (!HasValidSeekMetrics(snapEntry?.Snapshot))
            {
                _viewModel.CurrentTimeMs = _displayedTimeMs;
                return;
            }

            long appliedTime = snapEntry?.TimeMs ?? 0;
            await ApplyInSessionAsync(sessionId, () =>
            {
                if (snapEntry is not null) ReplayRunner.ApplySnapshot(_buffer, _parser, snapEntry.Snapshot);
                else _buffer.Clear();
                _displayedTimeMs = appliedTime;
                _viewModel.CurrentTimeMs = appliedTime;
            });

            bool reachedTarget = false;
            using var seekCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            seekCts.CancelAfter(TimeSpan.FromSeconds(2));
            try
            {
                var runner = new ReplayReader(_filePath);
                var result = await runner.RunWithResultAsync(
                    onDataCallback: data => ApplyInSessionAsync(sessionId, () => _parser.Process(System.Text.Encoding.UTF8.GetString(data))),
                    onHeaderCallback: header => ApplyHeaderMetricsAsync(sessionId, header, snapEntry is null),
                    onSixelCellSizeCallback: (w, h) => ApplyCellMetricsAsync(sessionId, w, h),
                    onResizeCallback: (w, h) => ApplyInSessionAsync(sessionId, () => _buffer.Resize(w, h)),
                    onTimeUpdate: time => { appliedTime = Math.Max(appliedTime, time); return Task.CompletedTask; },
                    options: new ReplayRunOptions { StopAtTimeMs = targetTimeMs },
                    minTimeMs: snapEntry?.TimeMs ?? 0,
                    ct: seekCts.Token);
                appliedTime = Math.Max(appliedTime, result.LastGoodOffset);
                reachedTarget = !seekCts.IsCancellationRequested && !result.Truncated;
            }
            catch (OperationCanceledException) when (seekCts.IsCancellationRequested) { }
            catch (Exception ex) { TerminalLogger.Error($"[ReplayWindow] Seek Error: {ex.Message}"); }
            await ApplyInSessionAsync(sessionId, () =>
            {
                _displayedTimeMs = reachedTarget ? targetTimeMs : appliedTime;
                _viewModel.CurrentTimeMs = _displayedTimeMs;
            });
        }

        private async Task PlaybackLoop(int sessionId, CancellationToken ct)
        {
            if (_filePath == null || _viewModel == null || _buffer == null || _parser == null) return;

            try
            {
                var runner = new ReplayReader(_filePath);
                long startTimeMs = _viewModel.CurrentTimeMs;

                // Determine starting state (Snapshot)
                var snapEntry = _viewModel.GetNearestSnapshotEntry(startTimeMs);
                long minTime = snapEntry?.TimeMs ?? 0;

                if (snapEntry != null)
                {
                    await ApplyInSessionAsync(sessionId, () => ReplayRunner.ApplySnapshot(_buffer, _parser, snapEntry.Snapshot));
                }
                else
                {
                    // No snapshot before this point, clear and start from 0
                    if (minTime == 0) await ApplyInSessionAsync(sessionId, () => _buffer.Clear());
                }

                await runner.RunAsync(
                    onDataCallback: async (data) =>
                    {
                        var text = System.Text.Encoding.UTF8.GetString(data);
                        await ApplyInSessionAsync(sessionId, () => _parser.Process(text));
                    },
                    onHeaderCallback: header => ApplyHeaderMetricsAsync(sessionId, header, snapEntry is null),
                    onSixelCellSizeCallback: (w, h) => ApplyCellMetricsAsync(sessionId, w, h),
                    onResizeCallback: async (w, h) =>
                    {
                        await ApplyInSessionAsync(sessionId, () => _buffer.Resize(w, h));
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
                                _displayedTimeMs = timeMs;
                                _viewModel.CurrentTimeMs = timeMs;
                            }
                        });
                    },
                    realtime: true,
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
                if (_playbackSessionGate.IsCurrent(sessionId))
                {
                    Dispatcher.UIThread.Post(() => { if (_playbackSessionGate.IsCurrent(sessionId)) StopPlayback(); });
                }
            }
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
