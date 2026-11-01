using System.Text;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public class KittyClipboardReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Legacy_and_complete_checkpoints_preserve_the_mode_without_a_clipboard_service(bool legacy)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(12, 4);
            var parser = new AnsiParser(live) { KittyClipboardEnabled = true };
            parser.Process("\x1b[?5522h");
            using (var recorder = new PtyRecorder(path, 12, 4))
            {
                recorder.RecordSnapshot(live, legacy ? null : parser);
                byte[] tail = Encoding.UTF8.GetBytes("\x1b[?5522l");
                recorder.RecordChunkAt(100, tail, tail.Length);
            }
            var restored = new TerminalBuffer(12, 4);
            var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(
                data => { restoredParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                    Assert.True(restored.Modes.IsClipboardPasteMode);
                    Assert.Null(restoredParser.OnKittyClipboardPacket);
                    Assert.False(restoredParser.KittyClipboardEnabled);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.False(restored.Modes.IsClipboardPasteMode);
        }
        finally { File.Delete(path); }
    }
}
