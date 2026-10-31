using System.Text.Json;
using Ntilde.Replay;

namespace Ntilde.VT.Tests;

public sealed class ReplayCheckpointPositionTests
{
    [Theory]
    [InlineData(false, "\n")]
    [InlineData(false, "\r\n")]
    [InlineData(true, "\n")]
    [InlineData(true, "\r\n")]
    public async Task Resume_line_excludes_checkpoint_prefix_but_keeps_same_timestamp_output(bool v1, string newline)
    {
        string path = Path.GetTempFileName();
        try
        {
            var lines = new List<string>();
            if (!v1) lines.Add(Header());
            lines.Add(Data(0, "before", v1));
            lines.Add("");
            if (!v1) lines.Add(JsonSerializer.Serialize(new ReplayEvent
            {
                Type = "snapshot",
                TimeOffsetMs = 0,
                Snapshot = new ReplaySnapshot { Cols = 10, Rows = 5 }
            }, ReplayJsonContext.Default.ReplayEvent));
            int checkpointLine = lines.Count;
            lines.Add(Data(0, "after", v1));
            lines.Add(Data(10, "end", v1));
            File.WriteAllText(path, string.Join(newline, lines)); // final record has no newline
            var output = new List<string>();
            var times = new List<long>();
            int headers = 0, resizes = 0, snapshots = 0;
            var result = await new ReplayReader(path).RunWithResultAsync(
                bytes => { output.Add(System.Text.Encoding.UTF8.GetString(bytes)); return Task.CompletedTask; },
                onHeaderCallback: _ => { headers++; return Task.CompletedTask; },
                onResizeCallback: (_, _) => { resizes++; return Task.CompletedTask; },
                onSnapshotCallback: _ => { snapshots++; return Task.CompletedTask; },
                onTimeUpdate: time => { times.Add(time); return Task.CompletedTask; },
                options: new ReplayRunOptions { StartAfterLineNumber = checkpointLine, ApplyHeaderResize = false },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "after", "end" }, output);
            Assert.Equal(new[] { 0L, 10L }, times);
            Assert.Equal(v1 ? 0 : 1, headers);
            Assert.Equal(0, resizes);
            Assert.Equal(0, snapshots);
            Assert.Equal(2, result.EventsProcessed);
            Assert.Equal(10, result.LastGoodOffset);

            output.Clear();
            result = await new ReplayReader(path).RunWithResultAsync(
                bytes => { output.Add(System.Text.Encoding.UTF8.GetString(bytes)); return Task.CompletedTask; },
                options: new ReplayRunOptions { StartAfterLineNumber = checkpointLine, StopAtTimeMs = 0 },
                ct: TestContext.Current.CancellationToken);
            Assert.Equal(new[] { "after" }, output);
            Assert.Equal(1, result.EventsProcessed);
            Assert.Equal(0, result.LastGoodOffset);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(1, 1)]
    public async Task Invalid_resume_options_are_rejected_before_header_or_data_callbacks(long line, long byteOffset)
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, Header() + "\n" + Data(0, "X", false));
            int callbacks = 0;
            await Assert.ThrowsAnyAsync<ArgumentException>(() => new ReplayReader(path).RunWithResultAsync(
                _ => { callbacks++; return Task.CompletedTask; },
                onHeaderCallback: _ => { callbacks++; return Task.CompletedTask; },
                onResizeCallback: (_, _) => { callbacks++; return Task.CompletedTask; },
                options: new ReplayRunOptions { StartAfterLineNumber = line }, startOffsetBytes: byteOffset,
                ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, callbacks);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Resuming_after_a_line_still_validates_the_header_metrics()
    {
        string path = Path.GetTempFileName();
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader
            {
                Type = ReplayHeader.TypeToken,
                Cols = 10,
                Rows = 5,
                SixelCellWidthPx = 10,
                SixelCellHeightPx = null
            }, ReplayJsonContext.Default.ReplayHeader);
            File.WriteAllText(path, header + "\n" + Data(0, "X", false));
            int callbacks = 0;
            await Assert.ThrowsAsync<InvalidDataException>(() => new ReplayReader(path).RunWithResultAsync(
                _ => { callbacks++; return Task.CompletedTask; },
                onHeaderCallback: _ => { callbacks++; return Task.CompletedTask; },
                options: new ReplayRunOptions { StartAfterLineNumber = 1 }, ct: TestContext.Current.CancellationToken));
            Assert.Equal(0, callbacks);
        }
        finally { File.Delete(path); }
    }

    private static string Header() => JsonSerializer.Serialize(
        new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 10, Rows = 5 }, ReplayJsonContext.Default.ReplayHeader);
    private static string Data(long time, string text, bool v1)
    {
        string data = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));
        return v1 ? JsonSerializer.Serialize(new { t = time, d = data })
            : JsonSerializer.Serialize(new ReplayEvent { Type = "data", TimeOffsetMs = time, Data = data }, ReplayJsonContext.Default.ReplayEvent);
    }
}
