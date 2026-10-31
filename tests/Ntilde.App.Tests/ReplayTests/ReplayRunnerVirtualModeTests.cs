using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;
using Ntilde.Replay;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Ntilde.Tests.ReplayTests
{
    public class ReplayRunnerVirtualModeTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task A_seek_boundary_includes_the_target_event_and_excludes_later_events(bool legacy)
        {
            string path = Path.GetTempFileName();
            try
            {
                var lines = new List<string>();
                if (!legacy) lines.Add(System.Text.Json.JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 10, Rows = 5 }, Ntilde.VT.ReplayJsonContext.Default.ReplayHeader));
                lines.Add("{\"t\":10,\"type\":\"data\",\"d\":\"QQ==\"}");
                lines.Add("{\"t\":20,\"type\":\"data\",\"d\":\"Qg==\"}");
                File.WriteAllLines(path, lines);
                var output = new List<byte>();
                var result = await new ReplayRunner(path).RunWithResultAsync(data => { output.AddRange(data); return Task.CompletedTask; },
                    options: new ReplayRunOptions { StopAtTimeMs = 10 }, ct: TestContext.Current.CancellationToken);
                Assert.Equal("A", Encoding.UTF8.GetString(output.ToArray()));
                Assert.Equal(10, result.LastGoodOffset);
                Assert.Equal(1, result.EventsProcessed);
                Assert.False(result.Truncated);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public async Task ReplayRunner_VirtualMode_ProcessesEventsAndMatchesSnapshot()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                const int cols = 100;
                const int rows = 30;
                const string payload = "alpha\r\nbeta\r\ngamma";

                var expectedBuffer = new TerminalBuffer(80, 24);
                var expectedParser = new AnsiParser(expectedBuffer);
                expectedBuffer.Resize(cols, rows);
                expectedParser.Process(payload);

                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    recorder.RecordResize(cols, rows);
                    byte[] bytes = Encoding.UTF8.GetBytes(payload);
                    recorder.RecordChunk(bytes, bytes.Length);
                    recorder.RecordSnapshot(expectedBuffer);
                }

                var actualBuffer = new TerminalBuffer(80, 24);
                var actualParser = new AnsiParser(actualBuffer);
                var runner = new ReplayRunner(tempFile);
                ReplayRunResult result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        actualParser.Process(Encoding.UTF8.GetString(data));
                        return Task.CompletedTask;
                    },
                    onResizeCallback: (newCols, newRows) =>
                    {
                        actualBuffer.Resize(newCols, newRows);
                        return Task.CompletedTask;
                    },
                    onSnapshotCallback: snapshot =>
                    {
                        actualBuffer.ApplySnapshot(snapshot);
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions
                    {
                        PlaybackMode = ReplayPlaybackMode.Virtual
                    }, ct: TestContext.Current.CancellationToken);

                Assert.False(result.Truncated);
                Assert.Equal(3, result.EventsProcessed);

                BufferSnapshot expected = BufferSnapshot.Capture(expectedBuffer, includeAttributes: true);
                BufferSnapshot actual = BufferSnapshot.Capture(actualBuffer, includeAttributes: true);
                Assert.Equal(expected.ToFormattedString(), actual.ToFormattedString());
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        /// <summary>
        /// Cancelling a run stops it quietly with what it replayed so far, as it always has, now that the reads honour the
        /// token: the read cancelled at the end of the buffered file reads as the end of it instead of throwing.
        /// </summary>
        [Fact]
        public async Task ReplayRunner_CancelledMidRun_ReturnsWhatItReplayed()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                using (var recorder = new PtyRecorder(tempFile, 80, 24, "pwsh.exe"))
                {
                    byte[] first = Encoding.UTF8.GetBytes("first");
                    recorder.RecordChunk(first, first.Length);
                    byte[] second = Encoding.UTF8.GetBytes("second");
                    recorder.RecordChunk(second, second.Length);
                }

                using var cts = new CancellationTokenSource();
                var replayed = new List<string>();
                var runner = new ReplayRunner(tempFile);
                ReplayRunResult result = await runner.RunWithResultAsync(
                    onDataCallback: data =>
                    {
                        replayed.Add(Encoding.UTF8.GetString(data));
                        cts.Cancel();
                        return Task.CompletedTask;
                    },
                    options: new ReplayRunOptions { PlaybackMode = ReplayPlaybackMode.Virtual },
                    ct: cts.Token);

                Assert.Equal(new[] { "first" }, replayed);
                Assert.Equal(1, result.EventsProcessed);
                Assert.False(result.Truncated);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }
    }
}
