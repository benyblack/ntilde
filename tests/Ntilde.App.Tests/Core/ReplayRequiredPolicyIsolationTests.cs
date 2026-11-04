using System.Reflection;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Ntilde.Replay;
using Ntilde.UI.Replay;
using Ntilde.VT;

namespace Ntilde.Tests.Core;

public sealed class ReplayRequiredPolicyIsolationTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unsupported_policy_is_rejected_before_snapshots_are_indexed(bool leadingBlank)
    {
        string path = Path.GetTempFileName();
        try
        {
            var header = new ReplayHeader { Type = ReplayHeader.TypeToken, RequiredSixelRasterPolicy = 1 };
            var snapshot = new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { State = new TerminalBuffer(20, 8).ExportState(0) } };
            string prefix = leadingBlank ? "\n \r\n" : "";
            await File.WriteAllTextAsync(path, prefix + JsonSerializer.Serialize(header, ReplayJsonContext.Default.ReplayHeader) + "\n" +
                JsonSerializer.Serialize(snapshot, ReplayJsonContext.Default.ReplayEvent), TestContext.Current.CancellationToken);
            var model = new ReplayViewModel(path);
            await model.Initialization;
            Assert.Equal(0, model.DurationMs);
            Assert.Null(model.GetNearestSnapshotEntry(100));
            Assert.Null(model.GetNearestSeekCheckpoint(100));
        }
        finally { File.Delete(path); }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Unsupported_policy_is_rechecked_before_cached_checkpoint_or_initial_state_restore(bool checkpoint, bool playback)
    {
        string path = Path.GetTempFileName();
        ReplayWindow? window = null;
        try
        {
            string header = JsonSerializer.Serialize(new ReplayHeader { Type = ReplayHeader.TypeToken, Cols = 20, Rows = 8 }, ReplayJsonContext.Default.ReplayHeader);
            var saved = new TerminalBuffer(20, 8);
            new AnsiParser(saved).Process("checkpoint");
            var item = checkpoint
                ? new ReplayEvent { Type = "snapshot", TimeOffsetMs = 100, Snapshot = new ReplaySnapshot { Cols = 20, Rows = 8, State = saved.ExportState(0) } }
                : new ReplayEvent { Type = "data", TimeOffsetMs = 100, Data = "WA==" };
            string data = JsonSerializer.Serialize(item, ReplayJsonContext.Default.ReplayEvent);
            await File.WriteAllTextAsync(path, header + "\n" + data + "\n", TestContext.Current.CancellationToken);
            window = new ReplayWindow(path);
            var model = (ReplayViewModel)window.DataContext!;
            await Ntilde.Mux.Tests.Support.TestWait.UntilAsync(() => model.DurationMs == 100, "the valid original recording was indexed");
            await File.WriteAllTextAsync(path, header[..^1] + ",\"required_sixel_raster\":1}\n" + data + "\n", TestContext.Current.CancellationToken);
            var buffer = Field<TerminalBuffer>(window, "_buffer");
            var parser = Field<AnsiParser>(window, "_parser");
            parser.Process("preserved\x1b[?25l\x1b[31;");
            byte[] before = Pair(buffer, parser);
            Task operation;
            if (playback)
            {
                model.CurrentTimeMs = 100;
                int session = Field<PlaybackSessionGate>(window, "_playbackSessionGate").BeginSession();
                operation = Invoke(window, "PlaybackLoop", session, CancellationToken.None);
            }
            else operation = Invoke(window, "PerformSeek", 100L, CancellationToken.None);
            await operation;
            Assert.Equal(before, Pair(buffer, parser));
        }
        finally { window?.Close(); File.Delete(path); }
    }

    private static T Field<T>(ReplayWindow window, string name) =>
        (T)typeof(ReplayWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

    private static Task Invoke(ReplayWindow window, string name, params object[] args) =>
        (Task)typeof(ReplayWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args)!;

    private static byte[] Pair(TerminalBuffer buffer, AnsiParser parser)
    {
        var state = buffer.ExportState(0);
        state.Parser = parser.ExportState();
        return TerminalStateSerializer.ToBytes(state);
    }
}
