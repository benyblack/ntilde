using System.Text;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.ReplayTests;

/// <summary>DECSCNM (?5, #831) survives a recording: through the stream, and through a mid-stream snapshot.</summary>
[Trait("Category", "Replay")]
public sealed class ReverseVideoReplayTests
{
    [Theory]
    [InlineData(false)] // legacy flat snapshot fields
    [InlineData(true)]  // complete state snapshot (with the parser)
    public async Task A_recorded_snapshot_restores_reverse_video_and_the_stream_toggles_it(bool withParser)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 4);
            var parser = new AnsiParser(live);
            parser.Process("\x1b[?5hflash");
            const string output = "\x1b[?5lnormal\x1b[?5h";
            using (var recorder = new PtyRecorder(path, 10, 4, "scripted"))
            {
                if (withParser) recorder.RecordSnapshot(live, parser);
                else recorder.RecordSnapshot(live);
                byte[] bytes = Encoding.UTF8.GetBytes(output);
                recorder.RecordChunk(bytes, bytes.Length);
            }
            parser.Process(output);

            var replay = new TerminalBuffer(10, 4);
            var replayParser = new AnsiParser(replay);
            bool sawSnapshot = false;
            await new ReplayRunner(path).RunAsync(
                onDataCallback: data =>
                {
                    Assert.True(sawSnapshot);
                    replayParser.Process(Encoding.UTF8.GetString(data));
                    return Task.CompletedTask;
                },
                onSnapshotCallback: snapshot =>
                {
                    Assert.True(snapshot.IsReverseVideo);
                    ReplayRunner.ApplySnapshot(replay, replayParser, snapshot);
                    Assert.True(replay.Modes.IsReverseVideo);
                    sawSnapshot = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);

            Assert.True(sawSnapshot);
            Assert.True(replay.Modes.IsReverseVideo);
            Assert.Equal(BufferSnapshot.Capture(live).Lines, BufferSnapshot.Capture(replay).Lines);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_snapshot_without_reverse_video_clears_a_mode_left_set_by_an_earlier_seek()
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(10, 4);
            using (var recorder = new PtyRecorder(path, 10, 4, "scripted"))
            {
                recorder.RecordSnapshot(live);
            }

            var replay = new TerminalBuffer(10, 4);
            replay.SetReverseVideo(true);
            await new ReplayRunner(path).RunAsync(
                onDataCallback: _ => Task.CompletedTask,
                onSnapshotCallback: snapshot => { replay.ApplySnapshot(snapshot); return Task.CompletedTask; },
                ct: TestContext.Current.CancellationToken);

            Assert.False(replay.Modes.IsReverseVideo);
        }
        finally { File.Delete(path); }
    }
}
