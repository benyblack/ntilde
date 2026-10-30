using System.Text;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.ReplayTests;

[Trait("Category", "Replay")]
public sealed class SixelReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recorded_initial_snapshot_and_changed_cell_metrics_preserve_layout(bool withSnapshot)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(live) { SixelCellSizeOverride = new SixelCellSize(12, 6) };
            const string first = "\x1b[5;1H\x1bPq!35~-~\x1b\\X";
            const string second = "\x1bPq!35~-~\x1b\\Z";
            using (var recorder = new PtyRecorder(path, 10, 5, "scripted", new RecordingCellMetrics(12, 6)))
            {
                if (withSnapshot) recorder.RecordSnapshot(live, parser);
                byte[] bytes = Encoding.UTF8.GetBytes(first);
                recorder.RecordChunk(bytes, bytes.Length);
                parser.Process(first);
                parser.SixelCellSizeOverride = new SixelCellSize(12, 12);
                recorder.RecordSixelCellSize(12, 12);
                bytes = Encoding.UTF8.GetBytes(second);
                recorder.RecordChunk(bytes, bytes.Length);
                parser.Process(second);
            }
            var replay = new TerminalBuffer(10, 5);
            var replayParser = new AnsiParser(replay);
            await new ReplayRunner(path).RunAsync(
                onDataCallback: data => { replayParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onHeaderCallback: header => { ReplayRunner.ApplyHeaderMetrics(replayParser, header); return Task.CompletedTask; },
                onSnapshotCallback: snapshot => { ReplayRunner.ApplySnapshot(replay, replayParser, snapshot); return Task.CompletedTask; },
                onSixelCellSizeCallback: (w, h) => { replayParser.SixelCellSizeOverride = new SixelCellSize(w, h); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal((live.CursorCol, live.CursorRow, live.TotalLines), (replay.CursorCol, replay.CursorRow, replay.TotalLines));
            Assert.Equal(BufferSnapshot.Capture(live).Lines, BufferSnapshot.Capture(replay).Lines);
            Assert.Equal(new SixelCellSize(12, 12), replayParser.SixelCellSizeOverride);
            using var stdout = new StringWriter();
            using var stderr = new StringWriter();
            Assert.Equal(0, ReplayCommand.Execute(["--replay", path], stdout, stderr));
            Assert.Equal(BufferSnapshot.Capture(live).ToFormattedString() + Environment.NewLine, stdout.ToString());
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Snapshot_only_metrics_override_an_unrelated_header_or_replay_font()
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(live) { SixelCellSizeOverride = new SixelCellSize(20, 6) };
            using (var recorder = new PtyRecorder(path, 10, 5)) recorder.RecordSnapshot(live, parser);
            var snapshot = System.Text.Json.JsonSerializer.Deserialize<ReplayEvent>(File.ReadLines(path).Last(), ReplayJsonContext.Default.ReplayEvent)!.Snapshot!;
            var replay = new TerminalBuffer(10, 5);
            var replayParser = new AnsiParser(replay) { CellHeight = 99 };
            ReplayRunner.ApplySnapshot(replay, replayParser, snapshot);
            Assert.Equal(new SixelCellSize(20, 6), replayParser.SixelCellSizeOverride);
            replayParser.Process("\x1b[5;1H\x1bPq!35~-~\x1b\\");
            Assert.Equal(7, replay.TotalLines);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(null, 6f)]
    [InlineData(12f, null)]
    [InlineData(0f, 6f)]
    [InlineData(float.NaN, 6f)]
    public void Malformed_snapshot_metrics_are_rejected_before_changing_the_buffer(float? width, float? height)
    {
        var buffer = new TerminalBuffer(10, 5);
        var parser = new AnsiParser(buffer);
        var snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, SixelCellWidthPx = width, SixelCellHeightPx = height };
        Assert.Throws<InvalidDataException>(() => ReplayRunner.ApplySnapshot(buffer, parser, snapshot));
        Assert.Equal((10, 5), (buffer.Cols, buffer.Rows));
    }

    [Fact]
    public async Task Flight_export_advances_initial_metrics_when_the_change_event_is_evicted()
    {
        string path = Path.GetTempFileName();
        try
        {
            var ring = new FlightRecordingBuffer(64, 10, 5, initialSixelCellSize: new RecordingCellMetrics(10, 20));
            ring.RecordSixelCellSize(12, 6);
            const string image = "\x1b[5;1H\x1bPq!35~-~\x1b\\";
            byte[] bytes = Encoding.UTF8.GetBytes(image);
            ring.RecordChunk(bytes, bytes.Length);
            ring.RecordChunk(bytes, bytes.Length); // Evicts the change and first chunk under the shared budget.
            ring.ExportTo(path, "scripted");
            var buffer = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(buffer);
            await new ReplayRunner(path).RunAsync(
                onDataCallback: data => { parser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onHeaderCallback: header => { ReplayRunner.ApplyHeaderMetrics(parser, header); return Task.CompletedTask; },
                onSixelCellSizeCallback: (w, h) => { parser.SixelCellSizeOverride = new SixelCellSize(w, h); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(new SixelCellSize(12, 6), parser.SixelCellSizeOverride);
            Assert.Equal(7, buffer.TotalLines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_recorded_display_mode_snapshot_resumes_without_inventing_image_scrolls()
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 5);
            var parser = new AnsiParser(live);
            parser.Process("\x1b[?80h\x1b[4;3H");
            string output = "\x1bPq!30~-~\x1b\\X\x1b[?80l\x1b[5;1H\x1bPq!30~-~\x1b\\Z";
            using (var recorder = new PtyRecorder(path, 10, 5, "scripted"))
            {
                recorder.RecordSnapshot(live);
                byte[] bytes = Encoding.UTF8.GetBytes(output);
                recorder.RecordChunk(bytes, bytes.Length);
            }
            parser.Process(output);
            var replay = new TerminalBuffer(10, 5);
            var replayParser = new AnsiParser(replay);
            bool sawSnapshot = false;
            await new ReplayRunner(path).RunAsync(
                onDataCallback: data => { replayParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    replay.ApplySnapshot(snapshot);
                    Assert.True(replay.Modes.IsSixelDisplayMode);
                    sawSnapshot = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(sawSnapshot);
            Assert.Equal((live.CursorCol, live.CursorRow, live.TotalLines), (replay.CursorCol, replay.CursorRow, replay.TotalLines));
            Assert.Equal(BufferSnapshot.Capture(live).Lines, BufferSnapshot.Capture(replay).Lines);
        }
        finally { File.Delete(path); }
    }
}
