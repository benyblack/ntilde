using System.Text;
using Ntilde.Replay;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests.ReplayTests;

[Trait("Category", "Replay")]
public sealed class SixelReplayTests
{
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
