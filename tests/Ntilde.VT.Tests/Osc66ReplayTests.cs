using System.Text;
using Ntilde.Replay;
using Ntilde.VT.Export;

namespace Ntilde.VT.Tests;

public sealed class Osc66ReplayTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "Replay")]
    public async Task Recorded_checkpoint_restores_geometry_and_combining_tail(bool legacy)
    {
        string path = Path.GetTempFileName();
        try
        {
            var live = new TerminalBuffer(12, 4);
            var parser = new AnsiParser(live);
            parser.Process("\x1b]66;s=2:w=2:n=1:d=2;AB\a");
            using (var recorder = new PtyRecorder(path, 12, 4))
            {
                recorder.RecordSnapshot(live, legacy ? null : parser);
                byte[] tail = Encoding.UTF8.GetBytes("\u0301!");
                recorder.RecordChunkAt(100, tail, tail.Length);
            }
            parser.Process("\u0301!");
            var restored = new TerminalBuffer(12, 4);
            var restoredParser = new AnsiParser(restored);
            bool applied = false;
            await new ReplayRunner(path).RunAsync(
                data => { restoredParser.Process(Encoding.UTF8.GetString(data)); return Task.CompletedTask; },
                onSnapshotCallback: snapshot =>
                {
                    Assert.Equal(legacy, snapshot.TextSizing != null);
                    if (!legacy) Assert.NotNull(snapshot.State?.TextSizing);
                    ReplayRunner.ApplySnapshot(restored, restoredParser, snapshot);
                    applied = true;
                    return Task.CompletedTask;
                }, ct: TestContext.Current.CancellationToken);
            Assert.True(applied);
            Assert.Equal(TerminalExporter.ExportToPlainText(live), TerminalExporter.ExportToPlainText(restored));
            restored.Lock.EnterReadLock();
            try
            {
                var piece = Assert.IsType<CellTextSizing>(restored.GetTextSizingAbsolute(3, 1));
                Assert.Equal("AB\u0301", piece.Block.Text);
                Assert.Equal(new TextSizingOptions(2, 2, 1, 2, 0, 0), piece.Block.Options);
            }
            finally { restored.Lock.ExitReadLock(); }
        }
        finally { File.Delete(path); }
    }
}
