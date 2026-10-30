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
            parser.SixelCellSizeOverride = new SixelCellSize(13, 7);
            parser.Process("preserved");
            var before = BufferSnapshot.Capture(buffer);
            await (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L])!;
            Assert.Equal((80, 24), (buffer.Cols, buffer.Rows));
            Assert.Equal(before.Lines, BufferSnapshot.Capture(buffer).Lines);
            Assert.Equal(new SixelCellSize(13, 7), parser.SixelCellSizeOverride);
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
            var seek = (Task)typeof(ReplayWindow).GetMethod("PerformSeek", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, [100L])!;
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
