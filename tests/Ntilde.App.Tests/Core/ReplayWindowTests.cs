using Ntilde.Replay;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Ntilde.Shell;
using Ntilde.UI.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class ReplayWindowTests
{
    [AvaloniaFact]
    public async Task A_restored_snapshot_keeps_its_geometry_until_an_in_stream_resize()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            var initial = new TerminalBuffer(20, 6);
            var parser = new AnsiParser(initial) { SixelCellSizeOverride = new SixelCellSize(20, 6) };
            using (var recorder = new PtyRecorder(path, 10, 5))
            {
                recorder.RecordSnapshot(initial, parser);
                byte[] data = System.Text.Encoding.UTF8.GetBytes("abcdefghijklmnop");
                recorder.RecordChunkAt(100, data, data.Length);
                recorder.RecordResizeAt(200, 30, 7);
            }
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 200, "the resized snapshot was indexed");
            var buffer = (TerminalBuffer)typeof(ReplayWindow).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (long target in new[] { 100L, 200L })
            {
                await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [target, CancellationToken.None])!;
                Assert.Equal(target == 100 ? 20 : 30, buffer.Cols);
                Assert.Equal("abcdefghijklmnop", BufferSnapshot.Capture(buffer).Lines[0]);
            }
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Corrupt_snapshot_cells_leave_seek_state_unchanged_and_do_not_escape_the_seek_task()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            var header = new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 };
            var bad = new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, CursorCol = 5, IsAltScreen = true, CellsBase64 = "not base64!" } };
            File.WriteAllLines(path, [System.Text.Json.JsonSerializer.Serialize(header, Ntilde.VT.ReplayJsonContext.Default.ReplayHeader), System.Text.Json.JsonSerializer.Serialize(bad, Ntilde.VT.ReplayJsonContext.Default.ReplayEvent)]);
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the corrupt snapshot was indexed");
            var parser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var buffer = (TerminalBuffer)typeof(ReplayWindow).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            parser.Process("preserved");
            var before = BufferSnapshot.Capture(buffer);
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L, CancellationToken.None])!;
            Assert.Equal((80, 24), (buffer.Cols, buffer.Rows));
            Assert.Equal(before.ToFormattedString(), BufferSnapshot.Capture(buffer).ToFormattedString());
            Assert.Equal(0, model.CurrentTimeMs);
            Assert.False(buffer.IsAltScreenActive);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Metrics_queued_by_an_old_session_cannot_overwrite_the_session_started_before_they_execute()
    {
        var window = new ReplayWindow(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rec"));
        try
        {
            var gate = (PlaybackSessionGate)typeof(ReplayWindow).GetField("_playbackSessionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var parser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            foreach (bool header in new[] { false, true })
            {
                int old = gate.BeginSession();
                Task? queued = null;
                using var posted = new ManualResetEventSlim();
                var poster = Task.Run(() =>
                {
                    string name = header ? "ApplyHeaderMetricsAsync" : "ApplyCellMetricsAsync";
                    object[] args = header ? [old, new ReplayHeader { SixelCellWidthPx = 10, SixelCellHeightPx = 20 }, true] : [old, 10f, 20f];
                    queued = (Task)typeof(ReplayWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;
                    posted.Set();
                }, TestContext.Current.CancellationToken);
                Assert.True(posted.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); // do not pump the UI yet
                Assert.False(queued!.IsCompleted);
                gate.BeginSession();
                parser.SixelCellSizeOverride = new SixelCellSize(20, 6);
                Dispatcher.UIThread.RunJobs();
                await queued;
                await poster;
                Assert.Equal(new SixelCellSize(20, 6), parser.SixelCellSizeOverride);
            }
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task A_cancelled_seek_keeps_the_applied_position_instead_of_committing_its_target()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            using (var recorder = new Ntilde.Replay.PtyRecorder(path, 10, 5))
            {
                recorder.RecordChunkAt(100, [65], 1);
            }
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the recording was indexed");
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L, cancelled.Token])!;
            Assert.Equal(0, model.CurrentTimeMs);
            Assert.Equal(0, window.FindControl<Slider>("TimelineSlider")!.Value);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_seek_does_not_apply_metrics_or_output_after_its_target()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            using (var recorder = new Ntilde.Replay.PtyRecorder(path, 10, 5, sixelCellSize: new RecordingCellMetrics(10, 20)))
            {
                recorder.RecordChunkAt(10, [65], 1);
                recorder.RecordSixelCellSizeAt(100, 20, 6);
                recorder.RecordChunkAt(100, [66], 1);
            }
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the recording was indexed");
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [50L, CancellationToken.None])!;
            var parser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var buffer = (TerminalBuffer)typeof(ReplayWindow).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(new SixelCellSize(10, 20), parser.SixelCellSizeOverride);
            Assert.Equal("A", BufferSnapshot.Capture(buffer).Lines[0]);
            Assert.Equal(50, model.CurrentTimeMs);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task A_superseded_playback_cannot_overwrite_seek_cell_metrics()
    {
        var window = new ReplayWindow(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".rec"));
        try
        {
            var gate = (PlaybackSessionGate)typeof(ReplayWindow).GetField("_playbackSessionGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var parser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            int oldSession = gate.BeginSession();
            gate.InvalidateCurrentSession();
            parser.SixelCellSizeOverride = new SixelCellSize(20, 6);
            await (Task)typeof(ReplayWindow).GetMethod("ApplyCellMetricsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [oldSession, 10f, 20f])!;
            Assert.Equal(new SixelCellSize(20, 6), parser.SixelCellSizeOverride);
            var header = new ReplayHeader { SixelCellWidthPx = 10, SixelCellHeightPx = 20 };
            await (Task)typeof(ReplayWindow).GetMethod("ApplyHeaderMetricsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [oldSession, header, true])!;
            Assert.Equal(new SixelCellSize(20, 6), parser.SixelCellSizeOverride);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Malformed_snapshot_metrics_leave_the_GUI_buffer_and_parser_unchanged()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            var header = new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 80, Rows = 24 };
            var bad = new ReplayEvent
            {
                Type = "snapshot",
                TimeOffsetMs = 100,
                Snapshot = new ReplaySnapshot
                {
                    Cols = 20,
                    Rows = 8,
                    SixelCellWidthPx = 12,
                    SixelCellHeightPx = null,
                }
            };
            File.WriteAllLines(path, [
                System.Text.Json.JsonSerializer.Serialize(header, Ntilde.VT.ReplayJsonContext.Default.ReplayHeader),
                System.Text.Json.JsonSerializer.Serialize(bad, Ntilde.VT.ReplayJsonContext.Default.ReplayEvent),
            ]);
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the malformed snapshot was indexed");
            var parser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var buffer = (TerminalBuffer)typeof(ReplayWindow).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [50L, CancellationToken.None])!;
            Assert.Equal(50, model.CurrentTimeMs);
            typeof(ReplayWindow).GetField("_isUserScrubbing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            window.FindControl<Slider>("TimelineSlider")!.Value = 100;
            Assert.Equal(100, model.CurrentTimeMs); // the real drag handler previews a rejected target
            typeof(ReplayWindow).GetField("_isUserScrubbing", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, false);
            parser.SixelCellSizeOverride = new SixelCellSize(13, 7);
            parser.Process("preserved");
            var before = BufferSnapshot.Capture(buffer);
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L, CancellationToken.None])!;
            Assert.Equal((80, 24), (buffer.Cols, buffer.Rows));
            Assert.Equal(before.Lines, BufferSnapshot.Capture(buffer).Lines);
            Assert.Equal(new SixelCellSize(13, 7), parser.SixelCellSizeOverride);
            Assert.Equal(50, model.CurrentTimeMs);
            Assert.Equal(50, window.FindControl<Slider>("TimelineSlider")!.Value);
            model.CurrentTimeMs = 100; // independently exercise rejection during playback
            await (Task)typeof(ReplayWindow).GetMethod("PlaybackLoop", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [0, CancellationToken.None])!;
            Assert.Equal((80, 24), (buffer.Cols, buffer.Rows));
            Assert.Equal(before.Lines, BufferSnapshot.Capture(buffer).Lines);
            Assert.Equal(new SixelCellSize(13, 7), parser.SixelCellSizeOverride);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [AvaloniaFact]
    public async Task Seeking_uses_snapshot_cell_metrics_instead_of_the_header_or_current_font()
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            var original = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(original) { SixelCellSizeOverride = new SixelCellSize(20, 6) };
            using (var recorder = new Ntilde.Replay.PtyRecorder(path, 10, 5, sixelCellSize: new Ntilde.Replay.RecordingCellMetrics(10, 20)))
            {
                recorder.RecordSnapshot(original, parser);
                byte[] data = System.Text.Encoding.UTF8.GetBytes("\x1b[5;1H\x1bPq!35~-~\x1b\\Z");
                recorder.RecordChunkAt(100, data, data.Length);
            }
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the replay index reached EOF");
            var seek = (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L, CancellationToken.None])!;
            await seek;
            var replayParser = (AnsiParser)typeof(ReplayWindow).GetField("_parser", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            var replayBuffer = (TerminalBuffer)typeof(ReplayWindow).GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(new SixelCellSize(20, 6), replayParser.SixelCellSizeOverride);
            Assert.Equal(7, replayBuffer.TotalLines);
            Assert.Equal(2, Assert.Single(replayBuffer.Images).CellWidth);
        }
        finally { window?.Close(); File.Delete(path); }
    }

    [Fact]
    public void TryAttachDeveloperTools_SwallowsDuplicateAttachmentFailure()
    {
        bool invoked = false;

        Exception? error = Record.Exception(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                invoked = true;
                throw new InvalidOperationException("Developer tools have already been attached. Multiple attachments are not supported.");
            }));

        Assert.True(invoked);
        Assert.Null(error);
    }

    [Fact]
    public void TryAttachDeveloperTools_RethrowsUnexpectedFailures()
    {
        Exception ex = Assert.Throws<InvalidOperationException>(() =>
            ReplayWindow.TryAttachDeveloperToolsForTest(() =>
            {
                throw new InvalidOperationException("Some other failure");
            }));

        Assert.Equal("Some other failure", ex.Message);
    }

    /// <summary>
    /// The buffer builds its rows on the Default theme before the replay's theme reaches it, so the
    /// theme has to remap them: setting it alone would leave unstyled text on Default's colours.
    /// </summary>
    [AvaloniaFact]
    public void A_replay_theme_reaches_the_rows_the_buffer_already_built()
    {
        // A path with nothing at it: the buffer is built before the recording is read, and a
        // real file would stay open in the view model's scan, which Windows will not delete.
        string recording = Path.Combine(Path.GetTempPath(), $"ntilde_replay_{Guid.NewGuid():N}.rec");
        var theme = new TerminalTheme
        {
            Name = "replay light",
            Foreground = TermColor.FromRgb(0x24, 0x29, 0x2F),
            Background = TermColor.FromRgb(0xFF, 0xFF, 0xFF),
        };
        var window = new ReplayWindow(recording, theme);
        try
        {
            var buffer = (TerminalBuffer)typeof(ReplayWindow)
                .GetField("_buffer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            TerminalCell cell;
            buffer.Lock.EnterReadLock();
            try
            {
                cell = buffer.GetCell(0, 0);
            }
            finally
            {
                buffer.Lock.ExitReadLock();
            }

            Assert.Equal(theme.Foreground.ToUint(), cell.Fg);
            Assert.Equal(theme.Background.ToUint(), cell.Bg);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// #644, Codex review of #651: a replay window hosts its own view, which only a pane used to retire, so its glyph
    /// atlas and row pictures waited for the finalizers after the window closed.
    /// </summary>
    [AvaloniaFact]
    public void Closing_the_window_disposes_its_views_render_caches()
    {
        string recording = Path.Combine(Path.GetTempPath(), $"ntilde_replay_{Guid.NewGuid():N}.rec");
        var window = new ReplayWindow(recording);
        TerminalView view = window.FindControl<TerminalView>("TermView")!;
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame();

        window.Close();
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.RenderCachesForTest.TryLease()); // the window let its view go

        // The compositor drops the closed window's last frame, and the draw operation holding the caches with it,
        // on its next tick; the app's other windows keep it ticking.
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();

        Assert.True(view.RenderCachesForTest.IsDisposed);
    }
}
